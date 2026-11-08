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
/// <para>An agent's attach, re-registration, detach, lapse and unregistration run one at a time under that agent's
/// serial gate, through the server call, so none undoes another or reaches the server out of order. A stop and the
/// switch going off end the attachment at once, outside it, since they run where nothing may wait on the server: the
/// attach they overtake is refused when the server answers it, and their unregistration takes the gate.</para>
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

    /// <summary>How many agents back a report about a dispatch is tried, as far as the server follows a link.</summary>
    const int MaxCarriedFrom = 16;

    const int GoneCap = 256, StoppedCap = 64, ReplacedCap = 256;

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
    const string NotHeldByModReason     = "not_held_by_mod";
    const string LapsedReason           = "attachment_lapsed";
    const string AttachmentsUnsupported = "attachments_unsupported";
    const string AttachmentEndedReason  = "attachment_ended";

    readonly ServerConnection      _server;
    readonly ModAttachmentStore    _store;
    readonly ModReportQueue        _reports;
    readonly Func<bool>            _steeringOn;
    readonly bool                  _offered;
    readonly TimeProvider          _time;
    readonly ILogger               _logger;
    readonly DaemonStatusNotifier? _status;
    readonly ITimer                _sweep;
    readonly KeyedAsyncGate        _serial    = new();
    readonly Lock                  _saveGate  = new();
    readonly Lock                  _sweepGate = new();
    Task                           _sweepRun  = Task.CompletedTask;

    readonly Lock _gate = new();
    readonly Dictionary<string, ModAttachedAgentRuntime> _live = new(StringComparer.Ordinal);
    readonly HashSet<string> _unregistered      = new(StringComparer.Ordinal);
    readonly HashSet<string> _unregisterPending = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, ModHandover> _handedOver = [];
    readonly Dictionary<string, string> _replaced    = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _carriedFrom = new(StringComparer.Ordinal);
    readonly List<ModStoppedPair> _stopped = [];
    readonly Dictionary<string, (string Reason, bool Retry, string ProcessKey)> _gone = new(StringComparer.Ordinal);
    readonly Queue<string> _goneOrder = new();

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
        _reports    = new ModReportQueue(server, logger) { Settled = ForgetHandover, Changed = Save };

        server.AdvertisesSteering = _offered;
        server.ConnectionLost    += DropAllHeld;
        server.Registered        += SweepOnRegistered;

        // A daemon that can't read its steering state still boots, steering nothing it held.
        try {
            Restore(store.Load());
        } catch (Exception ex) {
            LogRestoreFailed(ex);
            Clear();
        }

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
            if (Barred(agentId, session, processKey, replacesAgentId) is { } barred) return SteeringAttachReply.Refusal(barred, retry: false);
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
            var (reason, retry) = RefusalOf(answer.RefusedReason, agentId, processKey, replacesAgentId);
            LogAttachRefused(agentId, reason);

            return SteeringAttachReply.Refusal(reason, retry);
        }

        if (Admit(new ModAttachmentRecord(agentId, sessionId, processKey, replacesAgentId, _time.GetUtcNow())) is { } refused) {
            await UnregisterAsync(agentId);

            return SteeringAttachReply.Refusal(refused, retry: false);
        }

        LogAttached(agentId, replacesAgentId);

        return SteeringAttachReply.Attached(agentId, PollInterval);
    }

    /// <summary>Why the process may not attach as this agent whatever the server says, or null.</summary>
    string? Barred(string agentId, string session, string processKey, string? replacesAgentId) {
        if (_stopped.Contains(new ModStoppedPair(session, processKey))) return StoppedAnswer;

        // A process attaching again to an agent it carried on from would be a ghost once its replacement ends, unless it
        // is carrying on back, naming the attachment it is leaving.
        if (_replaced.TryGetValue(agentId, out var leftBy) && leftBy == processKey && !CarriesOnBack(processKey, replacesAgentId))
            return ReplacedAnswer;

        return null;
    }

    bool CarriesOnBack(string processKey, string? replacesAgentId) =>
        replacesAgentId is { } leaving && _live.TryGetValue(leaving, out var current) && current.ProcessKey == processKey;

    /// <summary>What the mod is told to do about a refusal. <c>session_not_owned</c> is "not now": the server's read of
    /// the owner can fail or miss. So is <c>session_attached</c>, except for an agent a process carried on from, which
    /// only a process carrying on back may take again.</summary>
    (string Reason, bool Retry) RefusalOf(string? refused, string agentId, string processKey, string? replacesAgentId) {
        bool ghost;
        lock (_gate) ghost = _replaced.ContainsKey(agentId) && !CarriesOnBack(processKey, replacesAgentId);

        return refused switch {
            AgentRegistrationRefusal.SessionAttached => (refused, !ghost),
            AgentRegistrationRefusal.SessionNotOwned or AgentRegistrationRefusal.ConnectionVanished or AgentRegistrationRefusal.NotADaemon => (refused, true),
            null => ("refused", false),
            _    => (refused, false),
        };
    }

    /// <summary>Why the registration may not stand, checked again now that the server has answered, or null once it
    /// is admitted. An agent the process carried on from leaves without an unregistration: the server ended it when this
    /// one registered, and ending it again would discard what it still waits behind.</summary>
    string? Admit(ModAttachmentRecord record) {
        lock (_gate) {
            if (!SteeringOn) return SteeringOffAnswer;

            if (Barred(record.AgentId, SessionIds.Canonical(record.SessionId)!, record.ProcessKey, record.ReplacesAgentId) is { } barred) return barred;

            if (_live.TryGetValue(record.AgentId, out var current) && current.ProcessKey == record.ProcessKey) {
                current.Reattach(record);
            } else {
                current?.DropHeld();
                _live[record.AgentId] = NewRuntime(record);
            }

            _unregistered.Remove(record.AgentId);
            _unregisterPending.Remove(record.AgentId);
            _gone.Remove(record.AgentId);
            _replaced.Remove(record.AgentId);

            if (record.ReplacesAgentId is { } left && left != record.AgentId) {
                _replaced[left]                = record.ProcessKey;
                _carriedFrom[record.AgentId] = left;

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

        return null;
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
            _reports.Enqueue(new ModInputReport(ModInputReport.Rejected, dispatchId, cmd.AgentId, Reason: AttachmentsUnsupported));
            Save();
            _ = _reports.FlushAsync();

            return true;
        }

        _ = runtime.SendDispatchedInputAsync(cmd.Text, dispatchId, waitForWrite: false);

        return true;
    }

    /// <summary>
    /// One poll from <paramref name="processKey"/>. Any call is a beat; a call while a turn runs, or while the mod holds a
    /// dispatch, asks the server nothing. Otherwise the oldest held dispatch is offered to the server, and handed over
    /// only once it confirms, and only while this is still the attachment it was offered for. A hold behind a dispatch the
    /// mod does not hold proves that one cannot land, since the mod records a dispatch before it submits one, so it is
    /// reported dropped and the next poll asks again; a report the mod made about it that has not reached the server yet
    /// is that record, so it is sent instead. Null when there is nothing to hand over.
    /// </summary>
    public async Task<SteeringNextReply?> NextAsync(string agentId, string processKey, bool idle, Guid? outstanding) {
        ModAttachedAgentRuntime? runtime;
        bool                     attached;

        lock (_gate) {
            attached = _live.TryGetValue(agentId, out runtime) && runtime.ProcessKey == processKey;
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

                        bool current;

                        lock (_gate) {
                            current = _live.GetValueOrDefault(agentId) == runtime;
                            if (current) _handedOver[held.DispatchId] = new ModHandover(agentId, runtime.ProcessKey);
                        }

                        // The attachment ended while the server answered: the dispatch never reaches the mod.
                        if (!current) {
                            _reports.Enqueue(new ModInputReport(ModInputReport.Rejected, held.DispatchId, agentId, Reason: AttachmentEndedReason));
                            Save();
                            _ = _reports.FlushAsync();

                            return GoneReply(agentId, processKey);
                        }

                        Save();

                        return SteeringNextReply.Handover(held.DispatchId, held.Text);

                    case InputHandoverOutcome.Drop:
                        runtime.RemoveHeld(held.DispatchId);

                        continue;

                    case InputHandoverOutcome.Hold when answer.BlockingDispatchId is { } blocking:
                        if (_reports.Holds(blocking)) _ = _reports.FlushAsync();
                        else await RejectLostAsync(runtime, blocking);

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

    SteeringNextReply GoneReply(string agentId, string processKey) {
        lock (_gate)
            return _gone.TryGetValue(agentId, out var gone) && gone.ProcessKey == processKey
                ? SteeringNextReply.GoneAs(gone.Reason, gone.Retry)
                : SteeringNextReply.GoneAs(LostAnswer, retry: true);
    }

    /// <summary>Reports a dispatch the server holds as With the mod, which the mod has no record of, under the agent it was
    /// sent to: only that agent's stream takes the report. With no record of that here, the agent polling and those its
    /// process carried on from are tried in turn, as far back as the server follows a link.</summary>
    async Task RejectLostAsync(ModAttachedAgentRuntime runtime, Guid dispatchId) {
        List<string> candidates;

        lock (_gate) candidates = _handedOver.GetValueOrDefault(dispatchId)?.AgentId is { } sentTo ? [sentTo] : CarriedFromChain(runtime);

        foreach (var agentId in candidates) {
            if (await _server.ReportAttachedInputRejectedAsync(dispatchId, agentId, NotHeldByModReason) != AttachedReportAnswer.Taken) continue;

            LogLostDispatchRejected(dispatchId, agentId);

            lock (_gate) _handedOver.Remove(dispatchId);
            Save();

            return;
        }
    }

    List<string> CarriedFromChain(ModAttachedAgentRuntime runtime) {
        List<string> chain = [runtime.AgentId];

        for (var next = runtime.Attachment.ReplacesAgentId;
             next is not null && chain.Count <= MaxCarriedFrom && !chain.Contains(next);
             next = _carriedFrom.GetValueOrDefault(next))
            chain.Add(next);

        return chain;
    }

    /// <summary>The mod saw a dispatch land as a row. Kept until the server takes it, and reported under the agent the
    /// dispatch was sent to. A beat only for the attachment <paramref name="processKey"/> holds.</summary>
    public void Link(string agentId, string? processKey, Guid dispatchId, string sessionId, Guid rowUuid) =>
        Report(agentId, processKey, dispatchId, sentTo => new ModInputReport(ModInputReport.Link, dispatchId, sentTo, sessionId, rowUuid));

    /// <summary>The mod knows a dispatch will not land, or cannot tell whether it did.</summary>
    public void Undelivered(string agentId, string? processKey, Guid dispatchId, bool rejected, string reason) =>
        Report(agentId, processKey, dispatchId,
            sentTo => new ModInputReport(rejected ? ModInputReport.Rejected : ModInputReport.Unconfirmed, dispatchId, sentTo, Reason: reason));

    void Report(string agentId, string? processKey, Guid dispatchId, Func<string, ModInputReport> build) {
        ModInputReport report;

        lock (_gate) {
            if (processKey is not null && _live.TryGetValue(agentId, out var runtime) && runtime.ProcessKey == processKey) runtime.Beat();

            report = build(_handedOver.GetValueOrDefault(dispatchId)?.AgentId ?? agentId);
        }

        _reports.Enqueue(report);
        Save();
        _ = _reports.FlushAsync();
    }

    internal Task FlushReportsAsync() => _reports.FlushAsync();

    void ForgetHandover(Guid dispatchId) {
        lock (_gate) _handedOver.Remove(dispatchId);
    }

    /// <summary>The session ended with its process. What the mod was handed is the server's to settle, once it learns
    /// the attachment ended, and nothing the process left can be a ghost any more.</summary>
    public async Task DetachAsync(string agentId, string? processKey) {
        if (await EndAsync(agentId, DetachedAnswer, retry: false, r => processKey is null || r.ProcessKey == processKey, ForgetProcess))
            LogDetached(agentId);
    }

    void ForgetProcess(ModAttachedAgentRuntime ended) {
        foreach (var (dispatchId, handover) in _handedOver.ToList())
            if (handover.ProcessKey == ended.ProcessKey && !_reports.Holds(dispatchId)) _handedOver.Remove(dispatchId);

        foreach (var (left, by) in _replaced.ToList())
            if (by == ended.ProcessKey) _replaced.Remove(left);

        foreach (var agentId in _carriedFrom.Keys.ToList())
            if (!_live.ContainsKey(agentId) && !_replaced.ContainsKey(agentId)) _carriedFrom.Remove(agentId);
    }

    /// <summary>A stop, from the server or <c>kcap agent stop</c>: the attachment ends, and the process is left exactly
    /// as it was. The session and process are barred, so an attach already on its way is refused once the server answers
    /// it, and stay barred until one of them changes; the server is told once that attach has settled. False for an
    /// agent id this does not hold.</summary>
    public bool Stop(string agentId) {
        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime)) return false;

            _stopped.Add(new ModStoppedPair(SessionIds.Canonical(runtime.Attachment.SessionId)!, runtime.ProcessKey));
            if (_stopped.Count > StoppedCap) _stopped.RemoveAt(0);

            EndHere(runtime, StoppedAnswer);
        }

        UnregisterEnded([agentId]);
        LogStopped(agentId);

        return true;
    }

    /// <summary>The switch went off: every attachment ends as a stop's does, but none is barred from attaching once it is
    /// on again.</summary>
    public void EndAll() {
        List<string> ended;

        lock (_gate) {
            ended = [.. _live.Keys];
            foreach (var agentId in ended) EndHere(_live[agentId], SteeringOffAnswer);
        }

        if (ended.Count > 0) UnregisterEnded(ended);
    }

    /// <summary>Called under the state lock.</summary>
    void EndHere(ModAttachedAgentRuntime runtime, string reason) {
        Forget(runtime, reason, retry: false);
        _unregisterPending.Add(runtime.AgentId);
    }

    void UnregisterEnded(IEnumerable<string> agentIds) {
        Save();
        _status?.Pulse();

        foreach (var agentId in agentIds) _ = UnregisterSoonAsync(agentId);
    }

    async Task UnregisterSoonAsync(string agentId) {
        try {
            await SendPendingUnregistrationAsync(agentId);
        } catch (Exception ex) {
            LogUnregisterFailed(agentId, ex.Message);
        }
    }

    /// <summary>Ends the attachment under its agent's serial gate, so an attach or a registration in flight finishes
    /// first and none starts until the server has been told. What is owed about its input goes first. False when no
    /// attachment <paramref name="ends"/> accepts is there by then.</summary>
    /// <param name="alsoForget">Runs under the state lock with the attachment just forgotten.</param>
    async Task<bool> EndAsync(
            string agentId, string reason, bool retry, Func<ModAttachedAgentRuntime, bool>? ends = null,
            Action<ModAttachedAgentRuntime>? alsoForget = null) {
        using var serial = await _serial.EnterAsync(agentId, CancellationToken.None);

        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime) || ends?.Invoke(runtime) == false) return false;

            Forget(runtime, reason, retry);
            alsoForget?.Invoke(runtime);
        }

        Save();
        _status?.Pulse();

        await _reports.FlushAsync();
        await UnregisterAsync(agentId);

        return true;
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

    /// <summary>An unregistration the server did not get, because it was away or the call failed, is sent by a later
    /// sweep.</summary>
    async Task UnregisterAsync(string agentId) {
        if (_server.IsReady) {
            try {
                await _server.AgentUnregisteredAsync(agentId).WaitAsync(RegistrationBudget, _time, CancellationToken.None);

                lock (_gate) _unregisterPending.Remove(agentId);

                return;
            } catch (Exception ex) {
                LogUnregisterFailed(agentId, ex.Message);
            }
        }

        lock (_gate) _unregisterPending.Add(agentId);
    }

    async Task SendPendingUnregistrationsAsync() {
        string[] pending;
        lock (_gate) pending = [.. _unregisterPending];

        foreach (var agentId in pending) await SendPendingUnregistrationAsync(agentId);
    }

    /// <summary>Sent once nothing else of the agent's is in flight, after what is owed about its input. One for an agent
    /// attached again since is dropped: the server now holds that agent's new attachment.</summary>
    async Task SendPendingUnregistrationAsync(string agentId) {
        using var serial = await _serial.EnterAsync(agentId, CancellationToken.None);

        lock (_gate) {
            if (_live.ContainsKey(agentId)) _unregisterPending.Remove(agentId);
            if (!_unregisterPending.Contains(agentId)) return;
        }

        await _reports.FlushAsync();
        await UnregisterAsync(agentId);
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

        var (reason, retry) = RefusalOf(answer.RefusedReason, agentId, record.ProcessKey, record.ReplacesAgentId);

        lock (_gate) {
            if (!_live.TryGetValue(agentId, out var runtime) || runtime.Attachment != record) return;

            Forget(runtime, reason, retry);
        }

        LogReRegistrationRefused(agentId, reason);
        Save();
        _status?.Pulse();
    }

    /// <summary>The reports go at once, whatever stage a sweep already running has reached.</summary>
    void SweepOnRegistered() {
        _ = _reports.FlushAsync();
        _ = SweepAsync();
    }

    /// <summary>Lapses attachments gone silent past <see cref="LapseBound"/>, then, while connected, registers what has not
    /// registered, unregisters what the server was not told of, and sends the reports it has not taken. One sweep at a
    /// time: a call while one runs gets that one.</summary>
    internal Task SweepAsync() {
        Task<Task> cold;
        Task       run;

        lock (_sweepGate) {
            if (!_sweepRun.IsCompleted) return _sweepRun;

            cold      = new Task<Task>(SweepOnceAsync);
            _sweepRun = run = cold.Unwrap();
        }

        cold.RunSynchronously(TaskScheduler.Default);

        return run;
    }

    async Task SweepOnceAsync() {
        try {
            await LapseAsync();

            if (!_server.IsReady) return;

            string[] pending;
            lock (_gate) pending = [.. _unregistered];

            foreach (var agentId in pending) await RegisterAsync(agentId);

            await SendPendingUnregistrationsAsync();
            await _reports.FlushAsync(sweep: true);
        } catch (Exception ex) {
            LogSweepFailed(ex);
        }
    }

    /// <summary>A lapsed attachment's mod can report nothing more, so what it was handed is reported Unconfirmed under
    /// the agent each was sent to, ahead of the unregistration that settles what the server holds for it.</summary>
    async Task LapseAsync() {
        string[] silent;
        lock (_gate) silent = [.. _live.Values.Where(r => r.SilentFor > LapseBound).Select(r => r.AgentId)];

        foreach (var agentId in silent)
            if (await EndAsync(agentId, LapsedAnswer, retry: true, r => r.SilentFor > LapseBound, QueueUnconfirmed))
                LogLapsed(agentId, (int)LapseBound.TotalSeconds);
    }

    void QueueUnconfirmed(ModAttachedAgentRuntime lapsed) {
        foreach (var (dispatchId, handover) in _handedOver)
            if (handover.ProcessKey == lapsed.ProcessKey && !_reports.Holds(dispatchId))
                _reports.Enqueue(new ModInputReport(ModInputReport.Unconfirmed, dispatchId, handover.AgentId, Reason: LapsedReason));
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

            foreach (var (dispatchId, handover) in file.HandedOver) _handedOver[Guid.Parse(dispatchId)] = handover;
            foreach (var (left, by) in file.Replaced) _replaced[left] = by;
            foreach (var (agentId, from) in file.CarriedFrom ?? []) _carriedFrom[agentId] = from;

            _stopped.AddRange(file.Stopped);
        }

        _reports.Restore(file.Reports);
    }

    void Clear() {
        lock (_gate) {
            _live.Clear();
            _unregistered.Clear();
            _handedOver.Clear();
            _replaced.Clear();
            _carriedFrom.Clear();
            _stopped.Clear();
        }
    }

    void Save() {
        lock (_saveGate) {
            ModAttachmentFile file;

            lock (_gate)
                file = new ModAttachmentFile(
                    [.. _live.Values.Select(r => r.Attachment)],
                    _handedOver.ToDictionary(h => h.Key.ToString("D"), h => h.Value),
                    _reports.Snapshot(),
                    new Dictionary<string, string>(_replaced, StringComparer.Ordinal),
                    [.. _stopped],
                    new Dictionary<string, string>(_carriedFrom, StringComparer.Ordinal));

            _store.TrySave(file);
        }
    }

    public ValueTask DisposeAsync() {
        _server.ConnectionLost -= DropAllHeld;
        _server.Registered     -= SweepOnRegistered;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attached agent {AgentId} could not be unregistered: {Why}; a later sweep tries again")]
    partial void LogUnregisterFailed(string agentId, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The steering sweep failed")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state could not be restored, so nothing it held is steered")]
    partial void LogRestoreFailed(Exception ex);
}
