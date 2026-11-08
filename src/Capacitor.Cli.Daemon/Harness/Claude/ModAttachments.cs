using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// The terminal Claude sessions this daemon steers through the kcap-steering mod, each an attached agent the server
/// registered with no launch, under an id derived from its session alone. The server decides every handover and keeps
/// every barrier: this asks it, and passes a held dispatch to the mod only once it says so. Nothing here waits on the
/// mod, so a server push is held and returns at once.
///
/// <para>Attachments, the dispatches handed to the mod, the reports owed about them, and the agents a process carried
/// on from outlive the daemon in <see cref="ModAttachmentStore"/>. Held dispatches never do, and go with the connection
/// they came on. An attached agent is never resumed, holds no daemon slot, and is never in the orchestrator's agents,
/// so a restart or an update does not wait for it.</para>
/// </summary>
internal sealed partial class ModAttachments : IAsyncDisposable {
    /// <summary>How often the mod polls, published in every attach answer so the lapse bound has one owner.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long an attachment may go without a request before it lapses: thirty polls, past a module reload or
    /// a stalled event loop, and short enough that what a killed Claude was sent is not left waiting for long.</summary>
    public static readonly TimeSpan LapseBound = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    static readonly TimeSpan RegistrationBudget = TimeSpan.FromSeconds(15);

    const int GoneCap = 256, StoppedCap = 64, ReplacedCap = 256, ReportCap = 1024;

    // What the mod is told. A refusal or a gone with no retry leaves it inert until its process or session changes.
    const string SteeringOffAnswer = "steering_off";
    const string HostedAnswer      = "hosted";
    const string StoppedAnswer     = "stopped";
    const string ReplacedAnswer    = "replaced";
    const string UnreachableAnswer = "unreachable";
    const string DetachedAnswer    = "detached";
    const string LapsedAnswer      = "lapsed";
    const string LostAnswer        = "lost";

    // What the server is told, as the reason a dispatch did not land or may not have.
    const string NotHeldByModReason      = "not_held_by_mod";
    const string LapsedReason            = "attachment_lapsed";
    const string AttachmentsUnsupported  = "attachments_unsupported";

    readonly ServerConnection      _server;
    readonly ModAttachmentStore    _store;
    readonly Func<bool>            _steeringOn;
    readonly bool                  _offered;
    readonly TimeProvider          _time;
    readonly ILogger               _logger;
    readonly DaemonStatusNotifier? _status;
    readonly ITimer                _sweep;
    readonly KeyedAsyncGate        _serial  = new();
    readonly SemaphoreSlim         _flushing = new(1, 1);
    readonly Lock                  _saveGate = new();

    readonly Lock _gate = new();
    readonly Dictionary<string, ModAttachedAgentRuntime> _live = new(StringComparer.Ordinal);
    readonly HashSet<string> _unregistered = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, ModHandover> _handedOver = [];
    readonly List<ModInputReport> _reports = [];
    readonly Dictionary<string, string> _replaced = new(StringComparer.Ordinal);
    readonly List<ModStoppedPair> _stopped = [];
    readonly Dictionary<string, (string Reason, bool Retry, string ProcessKey)> _gone = new(StringComparer.Ordinal);
    readonly Queue<string> _goneOrder = new();
    int _sweeping;

    /// <param name="steeringOn">The persisted switch.</param>
    /// <param name="offered">Whether steering is offered on this platform; <see cref="SteeringIpc.Offered"/> unless given.</param>
    public ModAttachments(
            ServerConnection server, ModAttachmentStore store, Func<bool> steeringOn, TimeProvider time, ILogger logger,
            DaemonStatusNotifier? status = null, bool? offered = null) {
        _server     = server;
        _store      = store;
        _steeringOn = steeringOn;
        _offered    = offered ?? SteeringIpc.Offered;
        _time       = time;
        _logger     = logger;
        _status     = status;

        server.AdvertisesSteering = _offered;
        server.ConnectionLost    += DropAllHeld;

        Restore(store.Load());

        _sweep = time.CreateTimer(state => _ = SweepAsync(), null, SweepInterval, SweepInterval);
    }

    /// <summary>Whether this daemon hosts the session, by its canonical id, or the process, by its pid; set by the
    /// orchestrator, which holds them.</summary>
    public Func<string, int, bool> Hosts { get; set; } = static (_, _) => false;

    bool SteeringOn => _offered && _steeringOn();

    /// <summary>The attached agent's id: derived from the session alone, so the same session gets the same id from any
    /// daemon process.</summary>
    public static string AgentIdFor(string canonicalSessionId) =>
        "mod-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSessionId)))[..16];

    public static bool IsAgentId(string? id) =>
        id is { Length: 20 } && id.StartsWith("mod-", StringComparison.Ordinal) && !id.AsSpan(4).ContainsAnyExcept(LowerHex);

    static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public bool Holds(string agentId) {
        lock (_gate) return _live.ContainsKey(agentId);
    }

    public IReadOnlyList<ModAttachmentRecord> Live {
        get {
            lock (_gate)
                return [.. _live.Values.Select(r => r.Attachment).OrderBy(a => a.AttachedAt).ThenBy(a => a.AgentId, StringComparer.Ordinal)];
        }
    }

    internal ModAttachedAgentRuntime? RuntimeOf(string agentId) {
        lock (_gate) return _live.GetValueOrDefault(agentId);
    }

    /// <summary>Registers the mod's session with the server and answers with the agent id it attached as, or why not.
    /// The same process attaching again gets the same id and keeps what is held for it.</summary>
    public async Task<SteeringAttachReply> AttachAsync(string sessionId, string processKey, string? replacesAgentId, CancellationToken ct) {
        if (!SteeringOn) return SteeringAttachReply.Refusal(SteeringOffAnswer, retry: false);

        var session = SessionIds.Canonical(sessionId)!;

        // Its terminal is already steered through the daemon's own PTY.
        if (Hosts(session, ModAttachedAgentRuntime.PidOf(processKey))) return SteeringAttachReply.Refusal(HostedAnswer, retry: false);

        var agentId = AgentIdFor(session);

        lock (_gate) {
            if (_stopped.Contains(new ModStoppedPair(session, processKey))) return SteeringAttachReply.Refusal(StoppedAnswer, retry: false);

            // A process that carried on from this agent attaching to it again would be a ghost once its replacement ends.
            if (_replaced.TryGetValue(agentId, out var leftBy) && leftBy == processKey) return SteeringAttachReply.Refusal(ReplacedAnswer, retry: false);
        }

        using var serial = await _serial.EnterAsync(agentId, ct);

        if (!_server.IsReady) return SteeringAttachReply.Refusal(UnreachableAnswer, retry: true);

        AgentRegisteredResponse answer;

        // Past here the client going away changes nothing: an attachment the mod never learns of lapses.
        try {
            answer = await _server.AttachedAgentRegisteredAsync(agentId, sessionId, processKey, replacesAgentId)
                .WaitAsync(RegistrationBudget, _time, CancellationToken.None);
        } catch (Exception ex) {
            LogRegistrationFailed(agentId, ex.Message);

            return SteeringAttachReply.Refusal(UnreachableAnswer, retry: true);
        }

        if (!answer.Registered) {
            var (reason, retry) = RefusalOf(answer.RefusedReason, agentId);
            LogAttachRefused(agentId, reason);

            return SteeringAttachReply.Refusal(reason, retry);
        }

        if (!Admit(new ModAttachmentRecord(agentId, sessionId, processKey, replacesAgentId, _time.GetUtcNow()))) {
            await UnregisterAsync(agentId);

            return SteeringAttachReply.Refusal(SteeringOffAnswer, retry: false);
        }

        LogAttached(agentId, replacesAgentId);

        return SteeringAttachReply.Attached(agentId, PollInterval);
    }

    /// <summary>What the mod is told to do about a refusal. <c>session_not_owned</c> is "not now": the server's read of
    /// the owner can fail or miss.</summary>
    (string Reason, bool Retry) RefusalOf(string? refused, string agentId) {
        bool replaced;
        lock (_gate) replaced = _replaced.ContainsKey(agentId);

        return refused switch {
            AgentRegistrationRefusal.SessionAttached => (refused, !replaced),
            AgentRegistrationRefusal.SessionNotOwned or AgentRegistrationRefusal.ConnectionVanished or AgentRegistrationRefusal.NotADaemon => (refused, true),
            null => ("refused", false),
            _    => (refused, false),
        };
    }

    /// <summary>False when the switch went off while the server was registering it. An agent the process carried on
    /// from leaves without an unregistration: the server ended it when this one registered, and ending it again would
    /// discard what it still waits behind.</summary>
    bool Admit(ModAttachmentRecord record) {
        lock (_gate) {
            if (!SteeringOn) return false;

            if (_live.TryGetValue(record.AgentId, out var current) && current.ProcessKey == record.ProcessKey) {
                current.Reattach(record);
            } else {
                current?.DropHeld();
                _live[record.AgentId] = NewRuntime(record);
            }

            _unregistered.Remove(record.AgentId);
            _gone.Remove(record.AgentId);
            _replaced.Remove(record.AgentId);

            if (record.ReplacesAgentId is { } left && left != record.AgentId) {
                _replaced[left] = record.ProcessKey;

                if (_live.Remove(left, out var carriedFrom)) {
                    carriedFrom.DropHeld();
                    _unregistered.Remove(left);
                    Tombstone(left, ReplacedAnswer, retry: false, carriedFrom.ProcessKey);
                }

                if (_replaced.Count > ReplacedCap) PruneReplaced();
            }
        }

        Save();
        _status?.Pulse();

        return true;
    }

    ModAttachedAgentRuntime NewRuntime(ModAttachmentRecord record) => new(record, () => _server.CurrentConnectionId, _time);

    /// <summary>A server push of input for an attached agent: held for the mod, or refused at once when the mod could not
    /// take it. False for an agent id this does not hold.</summary>
    public bool TakeInput(SendInputCommand cmd) {
        ModAttachedAgentRuntime? runtime;
        lock (_gate) _live.TryGetValue(cmd.AgentId, out runtime);

        if (runtime is null) return false;

        if (cmd.DispatchId is not { } dispatchId) {
            LogUnnamedInput(cmd.AgentId);

            return true;
        }

        // The mod submits text alone, so files would be left behind without a word.
        if (cmd.AttachmentIds is { Length: > 0 }) {
            _ = _server.ReportAttachedInputRejectedAsync(dispatchId, cmd.AgentId, AttachmentsUnsupported);

            return true;
        }

        _ = runtime.SendDispatchedInputAsync(cmd.Text, dispatchId, waitForWrite: false);

        return true;
    }

    /// <summary>
    /// One poll. Any call is a beat; a call while a turn runs, or while the mod holds a dispatch, asks the server
    /// nothing. Otherwise the oldest held dispatch is offered to the server, and handed over only once it confirms. A
    /// hold behind a dispatch the mod does not hold proves that one cannot land, since the mod records a dispatch before
    /// it submits one, so it is reported dropped and the next poll asks again. Null when there is nothing to hand over.
    /// </summary>
    public async Task<SteeringNextReply?> NextAsync(string agentId, string? processKey, bool idle, Guid? outstanding) {
        ModAttachedAgentRuntime? runtime;
        bool                     attached;

        lock (_gate) {
            attached = _live.TryGetValue(agentId, out runtime) && (processKey is null || runtime.ProcessKey == processKey);
            if (attached) runtime!.Beat();
        }

        if (!attached) return GoneReply(agentId, processKey);
        if (!idle || outstanding is not null || !runtime!.TryBeginHandover()) return null;

        try {
            for (var tries = runtime.HeldCount; tries > 0 && _server.IsReady; tries--) {
                if (runtime.OldestHeld(_server.CurrentConnectionId) is not { } held) return null;

                var answer = await _server.ConfirmInputHandoverAsync(held.DispatchId, agentId);

                switch (answer?.Outcome) {
                    case InputHandoverOutcome.HandedOver:
                        runtime.RemoveHeld(held.DispatchId);

                        lock (_gate) _handedOver[held.DispatchId] = new ModHandover(agentId, runtime.ProcessKey);
                        Save();

                        return SteeringNextReply.Handover(held.DispatchId, held.Text);

                    case InputHandoverOutcome.Drop:
                        runtime.RemoveHeld(held.DispatchId);

                        continue;

                    case InputHandoverOutcome.Hold when answer.BlockingDispatchId is { } blocking:
                        await RejectLostAsync(runtime, blocking);

                        return null;

                    default:
                        return null;
                }
            }

            return null;
        } finally {
            runtime.EndHandover();
        }
    }

    SteeringNextReply GoneReply(string agentId, string? processKey) {
        lock (_gate)
            return _gone.TryGetValue(agentId, out var gone) && (processKey is null || gone.ProcessKey == processKey)
                ? SteeringNextReply.GoneAs(gone.Reason, gone.Retry)
                : SteeringNextReply.GoneAs(LostAnswer, retry: true);
    }

    /// <summary>Reports a dispatch the server holds as With the mod, which the mod has no record of, under the agent it was
    /// sent to: only that agent's stream takes the report. With no record of that here, the agent polling and the one it
    /// carried on from are tried in turn.</summary>
    async Task RejectLostAsync(ModAttachedAgentRuntime runtime, Guid dispatchId) {
        string? sentTo;
        lock (_gate) sentTo = _handedOver.GetValueOrDefault(dispatchId)?.AgentId;

        string[] candidates = sentTo is not null ? [sentTo]
            : runtime.Attachment.ReplacesAgentId is { } carriedFrom ? [runtime.AgentId, carriedFrom]
            : [runtime.AgentId];

        foreach (var agentId in candidates) {
            if (await _server.ReportAttachedInputRejectedAsync(dispatchId, agentId, NotHeldByModReason) != true) continue;

            LogLostDispatchRejected(dispatchId, agentId);

            lock (_gate) _handedOver.Remove(dispatchId);
            Save();

            return;
        }
    }

    /// <summary>The mod saw a dispatch land as a row. Kept until the server takes it, and reported under the agent the
    /// dispatch was sent to.</summary>
    public void Link(string agentId, Guid dispatchId, string sessionId, Guid rowUuid) =>
        Report(agentId, dispatchId, sentTo => new ModInputReport(ModInputReport.Link, dispatchId, sentTo, sessionId, rowUuid));

    /// <summary>The mod knows a dispatch will not land, or cannot tell whether it did.</summary>
    public void Undelivered(string agentId, Guid dispatchId, bool rejected, string reason) =>
        Report(agentId, dispatchId, sentTo => new ModInputReport(rejected ? ModInputReport.Rejected : ModInputReport.Unconfirmed, dispatchId, sentTo, Reason: reason));

    void Report(string agentId, Guid dispatchId, Func<string, ModInputReport> build) {
        lock (_gate) {
            _live.GetValueOrDefault(agentId)?.Beat();

            var report = build(_handedOver.GetValueOrDefault(dispatchId)?.AgentId ?? agentId);

            if (!_reports.Contains(report)) {
                if (_reports.Count >= ReportCap) {
                    LogReportDropped(_reports[0].DispatchId, _reports[0].Kind);
                    _reports.RemoveAt(0);
                }

                _reports.Add(report);
            }
        }

        Save();
        _ = FlushReportsAsync();
    }

    /// <summary>Sends what the server has not taken yet, oldest first, stopping at the first that does not arrive.</summary>
    internal async Task FlushReportsAsync() {
        await _flushing.WaitAsync();

        try {
            while (_server.IsReady) {
                ModInputReport? next;
                lock (_gate) next = _reports.FirstOrDefault();

                if (next is null || !await SendAsync(next)) return;

                lock (_gate) {
                    _reports.Remove(next);
                    if (_reports.All(r => r.DispatchId != next.DispatchId)) _handedOver.Remove(next.DispatchId);
                }

                Save();
            }
        } finally {
            _flushing.Release();
        }
    }

    async Task<bool> SendAsync(ModInputReport report) => report.Kind switch {
        ModInputReport.Link        => await _server.ReportInputLinkAsync(new(report.DispatchId, report.AgentId, report.SessionId!, report.RowUuid!.Value)),
        ModInputReport.Unconfirmed => await _server.ReportInputUnconfirmedAsync(new(report.DispatchId, report.AgentId, report.Reason!)),
        _                          => await _server.ReportAttachedInputRejectedAsync(report.DispatchId, report.AgentId, report.Reason!) is not null,
    };

    /// <summary>The session ended with its process. What the mod was handed is the server's to settle, once it learns
    /// the attachment ended.</summary>
    public async Task DetachAsync(string agentId, string? processKey) {
        ModAttachedAgentRuntime? runtime;

        lock (_gate) {
            if (!_live.TryGetValue(agentId, out runtime) || processKey is not null && runtime.ProcessKey != processKey) return;

            Forget(runtime, DetachedAnswer, retry: false);

            foreach (var (dispatchId, handover) in _handedOver.ToList())
                if (handover.ProcessKey == runtime.ProcessKey && _reports.All(r => r.DispatchId != dispatchId)) _handedOver.Remove(dispatchId);

            foreach (var (left, by) in _replaced.ToList())
                if (by == runtime.ProcessKey) _replaced.Remove(left);
        }

        LogDetached(agentId);
        Save();
        _status?.Pulse();

        await UnregisterAsync(agentId);
    }

    /// <summary>A stop, from the server or <c>kcap agent stop</c>: the attachment ends, and the process is left exactly
    /// as it was. The same session and process are then refused until one of them changes. False for an agent id this
    /// does not hold.</summary>
    public async Task<bool> StopAsync(string agentId) {
        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime)) return false;

            Forget(runtime, StoppedAnswer, retry: false);

            _stopped.Add(new ModStoppedPair(SessionIds.Canonical(runtime.Attachment.SessionId)!, runtime.ProcessKey));
            if (_stopped.Count > StoppedCap) _stopped.RemoveAt(0);
        }

        LogStopped(agentId);
        Save();
        _status?.Pulse();

        await UnregisterAsync(agentId);

        return true;
    }

    /// <summary>The switch went off: every attachment ends as a stop's does, but none is barred from attaching once it is
    /// on again.</summary>
    public async Task EndAllAsync() {
        string[] ended;

        lock (_gate) {
            ended = [.. _live.Keys];

            foreach (var runtime in _live.Values.ToList()) Forget(runtime, SteeringOffAnswer, retry: false);
        }

        if (ended.Length == 0) return;

        Save();
        _status?.Pulse();

        await Task.WhenAll(ended.Select(UnregisterAsync));
    }

    void Forget(ModAttachedAgentRuntime runtime, string reason, bool retry) {
        _live.Remove(runtime.AgentId);
        _unregistered.Remove(runtime.AgentId);
        runtime.DropHeld();
        Tombstone(runtime.AgentId, reason, retry, runtime.ProcessKey);
    }

    void Tombstone(string agentId, string reason, bool retry, string processKey) {
        if (!_gone.ContainsKey(agentId)) _goneOrder.Enqueue(agentId);
        _gone[agentId] = (reason, retry, processKey);

        while (_gone.Count > GoneCap && _goneOrder.TryDequeue(out var oldest)) _gone.Remove(oldest);
    }

    void PruneReplaced() {
        var live = _live.Values.Select(r => r.ProcessKey).ToHashSet(StringComparer.Ordinal);

        foreach (var (left, by) in _replaced.ToList())
            if (!live.Contains(by)) _replaced.Remove(left);
    }

    async Task UnregisterAsync(string agentId) {
        if (!_server.IsReady) return;

        try {
            await _server.AgentUnregisteredAsync(agentId).WaitAsync(RegistrationBudget, _time, CancellationToken.None);
        } catch (Exception ex) {
            LogUnregisterFailed(agentId, ex.Message);
        }
    }

    /// <summary>Registers every attachment again, so the server moves it to this connection; run inside the
    /// registration, after <c>DaemonConnect</c> has reported them live. One the server now refuses ends here, and its
    /// mod is told why on its next poll.</summary>
    public async Task ReRegisterAsync() {
        string[] live;
        lock (_gate) live = [.. _live.Keys];

        foreach (var agentId in live) await RegisterAsync(agentId);
    }

    async Task RegisterAsync(string agentId) {
        using var serial = await _serial.EnterAsync(agentId, CancellationToken.None);

        ModAttachmentRecord record;

        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime)) return;

            record = runtime.Attachment;
        }

        AgentRegisteredResponse answer;

        try {
            answer = await _server.AttachedAgentRegisteredAsync(record.AgentId, record.SessionId, record.ProcessKey, record.ReplacesAgentId)
                .WaitAsync(RegistrationBudget, _time, CancellationToken.None);
        } catch (Exception ex) {
            lock (_gate) if (_live.ContainsKey(agentId)) _unregistered.Add(agentId);
            LogRegistrationFailed(agentId, ex.Message);

            return;
        }

        if (answer.Registered) {
            lock (_gate) _unregistered.Remove(agentId);

            return;
        }

        var (reason, retry) = RefusalOf(answer.RefusedReason, agentId);

        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime) || runtime.Attachment != record) return;

            Forget(runtime, reason, retry);
        }

        LogReRegistrationRefused(agentId, reason);
        Save();
        _status?.Pulse();
    }

    /// <summary>Lapses attachments gone silent past <see cref="LapseBound"/>, then, while connected, registers what has not
    /// registered and sends the reports the server has not taken.</summary>
    internal async Task SweepAsync() {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;

        try {
            await LapseAsync();

            if (!_server.IsReady) return;

            string[] pending;
            lock (_gate) pending = [.. _unregistered];

            foreach (var agentId in pending) await RegisterAsync(agentId);

            await FlushReportsAsync();
        } catch (Exception ex) {
            LogSweepFailed(ex);
        } finally {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>A lapsed attachment's mod can report nothing more, so what it was handed is reported Unconfirmed under
    /// the agent each was sent to, and the attachment is unregistered, which settles what the server holds for it.</summary>
    async Task LapseAsync() {
        List<string> lapsed = [];

        lock (_gate) {
            foreach (var runtime in _live.Values.Where(r => r.SilentFor > LapseBound).ToList()) {
                Forget(runtime, LapsedAnswer, retry: true);

                foreach (var (dispatchId, handover) in _handedOver)
                    if (handover.ProcessKey == runtime.ProcessKey && _reports.All(r => r.DispatchId != dispatchId))
                        _reports.Add(new ModInputReport(ModInputReport.Unconfirmed, dispatchId, handover.AgentId, Reason: LapsedReason));

                lapsed.Add(runtime.AgentId);
            }
        }

        if (lapsed.Count == 0) return;

        foreach (var agentId in lapsed) LogLapsed(agentId, (int)LapseBound.TotalSeconds);

        Save();
        _status?.Pulse();

        await FlushReportsAsync();

        foreach (var agentId in lapsed) await UnregisterAsync(agentId);
    }

    void DropAllHeld() {
        lock (_gate)
            foreach (var runtime in _live.Values) runtime.DropHeld();
    }

    void Restore(ModAttachmentFile file) {
        lock (_gate) {
            if (SteeringOn) {
                foreach (var attachment in file.Attachments) {
                    _live[attachment.AgentId] = NewRuntime(attachment);
                    _unregistered.Add(attachment.AgentId);
                }
            }

            foreach (var (dispatchId, handover) in file.HandedOver)
                if (Guid.TryParse(dispatchId, out var id)) _handedOver[id] = handover;

            _reports.AddRange(file.Reports.Where(Sendable));

            foreach (var (left, by) in file.Replaced) _replaced[left] = by;

            _stopped.AddRange(file.Stopped);
        }
    }

    static bool Sendable(ModInputReport report) => report.Kind switch {
        ModInputReport.Link                              => report is { SessionId: not null, RowUuid: not null },
        ModInputReport.Rejected or ModInputReport.Unconfirmed => report.Reason is not null,
        _                                                => false,
    };

    void Save() {
        lock (_saveGate) {
            ModAttachmentFile file;

            lock (_gate)
                file = new ModAttachmentFile(
                    [.. _live.Values.Select(r => r.Attachment)],
                    _handedOver.ToDictionary(h => h.Key.ToString("D"), h => h.Value),
                    [.. _reports],
                    new Dictionary<string, string>(_replaced, StringComparer.Ordinal),
                    [.. _stopped]);

            _store.TrySave(file);
        }
    }

    public ValueTask DisposeAsync() {
        _server.ConnectionLost -= DropAllHeld;

        return _sweep.DisposeAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} registered (replacing {ReplacesAgentId})")]
    partial void LogAttached(string agentId, string? replacesAgentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} refused by the server: {Reason}")]
    partial void LogAttachRefused(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} refused by the server on reconnect: {Reason}")]
    partial void LogReRegistrationRefused(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attached agent {AgentId} could not be registered: {Why}")]
    partial void LogRegistrationFailed(string agentId, string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} detached")]
    partial void LogDetached(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} stopped; the process is left as it was")]
    partial void LogStopped(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attached agent {AgentId} lapsed after {Seconds}s without a request")]
    partial void LogLapsed(string agentId, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatch {DispatchId} of attached agent {AgentId} reported dropped: its mod has no record of it")]
    partial void LogLostDispatchRejected(Guid dispatchId, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input for attached agent {AgentId} carried no dispatch id, so it can be neither held nor refused")]
    partial void LogUnnamedInput(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The steering report queue is full; dropped the {Kind} report of dispatch {DispatchId}")]
    partial void LogReportDropped(Guid dispatchId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attached agent {AgentId} could not be unregistered: {Why}")]
    partial void LogUnregisterFailed(string agentId, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The steering sweep failed")]
    partial void LogSweepFailed(Exception ex);
}
