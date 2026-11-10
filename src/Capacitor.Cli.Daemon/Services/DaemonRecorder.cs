using System.Collections.Concurrent;
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;
using Capacitor.Cli.Recording.Harness.Claude;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Records this daemon's hosted Claude sessions in its own process, so a hosted session needs no watcher. Off until
/// <see cref="DaemonConfig.RecordHostedClaude"/> is set and the server has said it can stop a source; while off it opens
/// no connection and takes no claim.
///
/// <para>A source's life is one claim and one list entry around one <see cref="RecorderHost"/> entry. Its claim is taken
/// under the spawn lock with the live-watcher check inside it, before the list entry and before the host hears of it,
/// because a host adding a source with no claim reads it as lost and silently records nothing. A source that finished
/// lets its claim go, and only once its key has left the host: a take behind a release while the key is still held is
/// refused by the host and leaves the claim with nothing beating it. A source that lost its claim touches it no more,
/// since it is someone else's. A shutdown lets go only of what no agent comes back for: the next boot picks up the rest.</para>
///
/// <para>A session a person disabled is refused at every way in, and <see cref="DisableSessionAsync"/> ends what is
/// already running. Those two meet without a lock, because the marker is on disk before the command asks: whoever takes a
/// source publishes it and then reads the marker in <see cref="Start"/>, and the disable reads what is published after
/// the marker went down, so one of them sees the other.</para>
/// </summary>
internal sealed partial class DaemonRecorder : IAsyncDisposable {
    // A lost spawn lock is another process in the same few file operations, and a launch waits behind these.
    const int TakeAttempts = 4;
    static readonly TimeSpan TakeRetryGap = TimeSpan.FromMilliseconds(50);

    // A launch's lane waits here, and the restore can hold a key for its whole window.
    static readonly TimeSpan ClaimWait = TimeSpan.FromSeconds(1);

    // A fresh claim that isn't this daemon's goes stale within the grace if its holder is dead.
    static readonly TimeSpan RestoreRetryGap = TimeSpan.FromSeconds(1);
    static readonly TimeSpan RestoreWindow   = RecorderClaims.Grace + TimeSpan.FromSeconds(5);

    // A hub round trip the connection has already asked for. The boot's resumes wait behind it, and only with a list to
    // pick up: an answer that takes longer leaves that list for the next boot.
    internal static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);

    /// <summary>Beside the shutdown's report to the server and no longer than it, so it adds nothing to a shutdown. Of
    /// this, 2 seconds stay back for the spool, which is what a host keeps of any bound from 4 seconds up.</summary>
    internal static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(5);

    /// <summary>How long <c>kcap disable</c> waits on what a session had under way. A person is waiting, and past this
    /// the command says what it couldn't confirm.</summary>
    internal static readonly TimeSpan DisableBound = TimeSpan.FromSeconds(5);

    readonly DaemonConfig _config;
    readonly CapacitorServer _server;
    readonly RecordingProbe _serverRecords;
    readonly Func<ISilenceableRecorderTransport> _connect;
    readonly Func<RecordedSource, Action<string>, Action, Action, IRecorderHousekeeping> _housekeeping;
    readonly TimeProvider _time;
    readonly ILogger _logger;
    readonly RecorderClaims _claims;
    readonly WatcherLiveness _liveness;
    readonly RecordedSourceList _list;
    readonly KeyedAsyncGate _keys = new();
    readonly ConcurrentDictionary<string, Tracked> _sources = new(StringComparer.Ordinal);
    readonly KeyedAsyncGate _agentGates = new();
    readonly ConcurrentDictionary<Task, string> _moves = new();
    readonly ConcurrentQueue<(RecordedSource Source, bool MayImport)> _letGo = new();
    readonly CancellationTokenSource _closing = new();
    readonly Lazy<RecorderHost> _host;

    // A first source creates the host, and a stop finding none has to know no source will.
    readonly Lock _starting = new();

    ISilenceableRecorderTransport? _transport;
    Func<string, bool>? _agentIsLive;
    int _stopping;
    int _disposed;

    /// <param name="connect">Builds the transport, for the first source. Nothing connects before that.</param>
    /// <param name="housekeeping">Builds a recorder's housekeeping, given its source, its log, its own stop and its beat.</param>
    public DaemonRecorder(
            DaemonConfig config, CapacitorServer server, RecordingProbe serverRecords, Func<ISilenceableRecorderTransport> connect,
            Func<RecordedSource, Action<string>, Action, Action, IRecorderHousekeeping> housekeeping, TimeProvider time, ILogger<DaemonRecorder> logger) {
        _config        = config;
        _server        = server;
        _serverRecords = serverRecords;
        _connect       = connect;
        _housekeeping  = housekeeping;
        _time          = time;
        _logger        = logger;

        var paths = WatcherPaths.FromEnvironment(config.ConfigRoot);
        _claims   = new RecorderClaims(paths.Directory, time);
        _liveness = new WatcherLiveness(paths, time);
        _list     = new RecordedSourceList(config.Store.StateDirectory(config.Name), logger);
        _host     = new Lazy<RecorderHost>(CreateHost);

        serverRecords.SaidNo += () => _ = Task.Run(LetEverySourceGoAsync);
    }

    /// <summary>The daemon as it runs: one provider router and one connection for every recorder.</summary>
    public static DaemonRecorder Compose(
            DaemonConfig config, CapacitorServer server, ServerConnection connection, ICapacitorHttpClient http,
            HarnessRegistry harnesses, ILoggerFactory loggers, TimeProvider time) {
        var logger      = loggers.CreateLogger<DaemonRecorder>();
        var router      = new GitProviderRouter(time);
        var accounts    = config.Accounts ?? Core.Accounts.AccountStore.Beside(config.Store);
        var hookCommand = config.KcapCliPath is { } cli ? GitHookProbe.CommandFor(cli) : null;

        return new DaemonRecorder(
            config, server, connection.RecordingProbe,
            () => {
                WarnOfMissingTools(config, logger);

                var refusals = new BearerRefusalLog(report => logger.LogWarning("Server refused the recording connection's bearer — {Report}", report));
                var transport = new ServerRecorderTransport(
                    DaemonHub.Build(config, async () => refusals.Presenting((await connection.Credentials.ResolveAsync(default)).Bearer), refusals.Refused, loggers, time),
                    time, message => logger.LogInformation("{Message}", message));

                connection.SignInChanged += transport.Restart;

                return transport;
            },
            (source, log, stop, beat) => new RecorderHousekeeping(
                source.Recording, source.SkipTitle, parentPid: null,
                new DaemonRecorderHost(config.ConfigRoot, hookCommand, time, stop, (session, vendor, path) => HandOffTitle(config, logger, session, vendor, path), log),
                config.ConfigRoot, config.Profiles, harnesses, http, router, time, accounts, config.Home, log, beat),
            time, logger);
    }

    /// <summary>Whether this daemon takes new sources now. Read on every use: the server's answer goes with its connection,
    /// and what is already recording carries on without it.</summary>
    public bool Enabled => IsOn && _serverRecords.Answer == true;

    /// <summary>Whether recording is switched on here at all, whatever the server has said.</summary>
    public bool IsOn => _config.RecordHostedClaude;

    /// <summary>Recording is on and the server hasn't answered, or its answer went with a connection. Nothing that turns
    /// on what it supports can be decided yet, and the next answer settles it.</summary>
    public bool Undecided => IsOn && _serverRecords.Answer is null;

    /// <summary>Whether <paramref name="key"/> is this daemon's to record, and not on its way out.</summary>
    public bool Holds(string key) => _sources.TryGetValue(key, out var tracked) && !IsLeaving(tracked);

    /// <summary>The keys being recorded now.</summary>
    internal IReadOnlyCollection<string> ActiveKeys => _host.IsValueCreated ? _host.Value.ActiveKeys : [];

    /// <summary>Takes <paramref name="source"/>'s claim ahead of its agent process, so the process's first hook finds it.
    /// True when the claim is this daemon's; false leaves the source to whatever a hook starts, as it does for a source
    /// on its way out, whose next hook hands it over. A caller with a true answer follows with
    /// <see cref="StartForAgent"/>, or <see cref="ReleaseClaim"/> if the agent never ran.</summary>
    public async Task<bool> ClaimAsync(RecordedSource source, CancellationToken ct) {
        using var bound  = new CancellationTokenSource(ClaimWait, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);

        IDisposable gate;

        try {
            gate = await _keys.EnterAsync(source.Key, either.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            LogClaimGaveUp(source.Key);

            return false;
        }

        using (gate) {
            return _sources.TryGetValue(source.Key, out var held) ? !IsLeaving(held) : await TakeAsync(source, ct) is not null;
        }
    }

    /// <summary>Starts recording a source <see cref="ClaimAsync"/> claimed, with its agent published. A source the last
    /// daemon left is already being recorded, and is now this agent's, moved to where this agent writes it.</summary>
    public void StartForAgent(RecordedSource source) {
        if (!_sources.TryGetValue(source.Key, out var tracked) || IsLeaving(tracked)) return;

        if (tracked.Running) {
            // The host goes on tailing the path it was given until the move, and the entry says so meanwhile.
            var tailed = tracked.Source.TranscriptPath;

            tracked.Source   = source with { TranscriptPath = tailed };
            tracked.Restored = false;
            _list.TryAdd(tracked.Source);

            if (tailed != source.TranscriptPath) _ = MoveAsync(tracked, source);

            return;
        }

        Start(tracked);
    }

    /// <summary>Lets go of a claim whose agent never ran. A source already running stays: it came from the last daemon,
    /// and a failed launch says nothing about what it has yet to send.</summary>
    public void ReleaseClaim(RecordedSource source) {
        if (_sources.TryGetValue(source.Key, out var tracked) && !tracked.Running) Forget(tracked);
    }

    /// <summary>Records a source a hook handed over, such as a subagent's or a session a hosted agent moved to. False
    /// when it can't: the hook then starts a watcher.</summary>
    public async Task<bool> TryRecordAsync(RecordedSource source, CancellationToken ct) {
        while (true) {
            Tracked? held;

            using (await _keys.EnterAsync(source.Key, ct)) {
                if (!_sources.TryGetValue(source.Key, out held)) return await TakeAsync(source, ct) is { } taken && Start(taken);

                if (held.Disabled) return false;
                if (!IsLeaving(held)) return true;
            }

            await held.Settled.Task.WaitAsync(ct);
        }
    }

    /// <summary>Moves a source to where its transcript turned out to be. Claude's project directory is computed ahead of
    /// the launch, and a path it spells differently would otherwise be tailed for ever with the session unrecorded.</summary>
    public async Task ObserveTranscriptAsync(RecordedSource found) {
        if (!_sources.TryGetValue(found.Key, out var tracked) || tracked.Source.TranscriptPath == found.TranscriptPath) return;
        if (File.Exists(tracked.Source.TranscriptPath)) return;

        await MoveAsync(tracked, found with { SkipTitle = tracked.Source.SkipTitle });
    }

    // One restart, and the server's position carries the source on from where it was. Under the agent's gate, which its
    // finish takes too, so the two never interleave: an agent finished by the time the replacement is up has that tail
    // drained, and nothing of it goes on recording. Known to the agent's finish from before the gate, which waits for it.
    async Task MoveAsync(Tracked from, RecordedSource to) {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _moves[done.Task] = to.AgentId;

        try {
            using var gate = await _agentGates.EnterAsync(to.AgentId, CancellationToken.None);

            LogTranscriptMoved(to.Key, to.TranscriptPath);
            await FinishAsync(to.Key, CancellationToken.None);

            if (!await TryRecordAsync(to, CancellationToken.None) || !from.AgentFinished) return;

            if (_sources.TryGetValue(to.Key, out var replacement) && replacement.Source.AgentId == to.AgentId) {
                replacement.AgentFinished = true;
                await FinishQuietlyAsync(to.Key, CancellationToken.None);
            }
        } finally {
            _moves.TryRemove(done.Task, out var _);
            done.SetResult();
        }
    }

    /// <summary>Makes the final drain of the source <paramref name="key"/> names and lets it go. Off the caller's thread,
    /// because a cancel runs the drain up to its first real wait, and <paramref name="ct"/> bounds only the waiting: the
    /// drain carries on without it.</summary>
    /// <returns>Whether this daemon held the source and it finished.</returns>
    /// <param name="hurried">The caller won't wait long, as a hook doesn't.</param>
    public async Task<bool> FinishAsync(string key, CancellationToken ct, bool hurried = false) {
        if (Volatile.Read(ref _stopping) != 0 || !_sources.TryGetValue(key, out var tracked) || !tracked.Running || tracked.Disabled) return false;

        await Task.Run(() => _host.Value.FinishAsync(key, ct, hurried), CancellationToken.None);
        await tracked.Settled.Task.WaitAsync(ct);

        return true;
    }

    /// <summary>Finishes every source of an exited agent, within <paramref name="budget"/>, once: a second call on the
    /// way through a teardown spends none of it on a drain already asked for. Never throws, and what the budget cuts off
    /// carries on: a tail landing behind the session's end reactivates it, and the server's sweep closes it again once
    /// the source has detached.</summary>
    public async Task FinishAgentAsync(string agentId, TimeSpan budget) {
        try {
            await FinishUnderGateAsync(agentId).WaitAsync(budget, _time);
        } catch (TimeoutException) {
            // The bound passed, and the finish carries on.
        }
    }

    // Under the agent's gate, so a move of its source is either over, its replacement the current source, or still to
    // come, and it then finds the source it moves from finished. The drains are waited for outside the gate, which a
    // second ask on the way through a teardown would otherwise spend its budget on.
    async Task FinishUnderGateAsync(string agentId) {
        Task[] finishing;

        using (await _agentGates.EnterAsync(agentId, CancellationToken.None)) {
            var keys = _sources.Where(source => source.Value.Source.AgentId == agentId && source.Value is { Running: true, AgentFinished: false, Disabled: false })
                .Select(source => source.Key).ToArray();

            foreach (var tracked in keys.Select(key => _sources.GetValueOrDefault(key)).OfType<Tracked>()) tracked.AgentFinished = true;

            finishing = [.. keys.Select(key => FinishQuietlyAsync(key, CancellationToken.None))];
        }

        await Task.WhenAll(finishing);

        // A move admitted behind this waits for the same old source and then drains its replacement's tail, and the
        // session's end follows this finish.
        while (_moves.Where(move => move.Value == agentId).Select(move => move.Key).ToArray() is { Length: > 0 } moving) await Task.WhenAll(moving);
    }

    /// <summary>Picks up what the last daemon left: each source from the line the server answers with, unless a live
    /// watcher holds it now. With recording off it lets all of it go, because a claim nobody is coming back for would
    /// keep a resumed session's hooks from starting a watcher. So it does when the capture scope is left to hooks: a
    /// source that was in scope when its hook handed it over needn't be now, and its next hook hands it over again. With
    /// no answer from the server by <see cref="AnswerWait"/> nothing is decided, and the answer does this when it lands.
    /// <see cref="SettleRestoredAsync"/> finishes what this lets go of.</summary>
    /// <param name="currentScope">Read only with something to pick up.</param>
    public async Task RestoreAsync(Func<Task<RecordingScope>> currentScope, CancellationToken ct) {
        var left = _list.Load();
        if (left.Count == 0) return;

        if (await ServerRecordsAsync(ct) is not { } records) {
            LogRestoreUnanswered(left.Count);

            var closing = _closing.Token;
            _ = Task.Run(() => RestoreOnAnswerAsync(currentScope, closing), CancellationToken.None);

            return;
        }

        await RestoreLeftAsync(left, records, currentScope, ct);
    }

    async Task RestoreLeftAsync(IReadOnlyList<RecordedSource> left, bool records, Func<Task<RecordingScope>> currentScope, CancellationToken ct) {
        var scope   = await currentScope();
        var restore = records && !scope.LeftToHooks;

        await Task.WhenAll(left.Select(source => restore ? RestoreOneAsync(source, ct) : LetGoAsync(source, scope, ct)));
    }

    // The probe keeps asking past the boot's wait, so the answer can land long after the settling.
    async Task RestoreOnAnswerAsync(Func<Task<RecordingScope>> currentScope, CancellationToken closing) {
        try {
            var records = await _serverRecords.Answered.WaitAsync(closing);
            if (Volatile.Read(ref _stopping) != 0) return;

            await RestoreLeftAsync(_list.Load(), records, currentScope, closing);

            if (Volatile.Read(ref _agentIsLive) is { } agentIsLive) await SettleRestoredAsync(agentIsLive);
        } catch (OperationCanceledException) {
            // The daemon is closing, and the next boot has the list.
        } catch (Exception ex) {
            LogLateRestoreFailed(ex);
        }
    }

    async Task<bool?> ServerRecordsAsync(CancellationToken ct) {
        if (!_config.RecordHostedClaude) return false;

        try {
            return await _serverRecords.Answered.WaitAsync(AnswerWait, _time, ct);
        } catch (TimeoutException) {
            return null;
        }
    }

    /// <summary>Finishes each restored source whose agent didn't come back, once the boot's resumes have run. One let go
    /// of that has no agent back has no hook coming and nothing to drain it, so it is marked for import, which is all that
    /// says its tail was lost.</summary>
    public async Task SettleRestoredAsync(Func<string, bool> agentIsLive) {
        Volatile.Write(ref _agentIsLive, agentIsLive);

        foreach (var tracked in _sources.Values.Where(source => source.Restored && !agentIsLive(source.Source.AgentId)))
            _ = FinishQuietlyAsync(tracked.Source.Key, CancellationToken.None);

        while (_letGo.TryDequeue(out var left)) await SettleLetGoAsync(left.Source, left.MayImport, agentIsLive);
    }

    /// <summary>Stops every source of a session a person disabled, its subagents' included, where each stands, for
    /// <c>kcap disable</c>. From here the session sends nothing: the transport refuses it ahead of the connection, so a
    /// source in a backoff, a final drain or stuck where no token reaches can't send when it comes round. What this can't
    /// recall is a call already made, so it waits for those within <see cref="DisableBound"/>, and the answer is all the
    /// command has to go on. Takes no key's gate, because a restore can hold one for a minute.</summary>
    public async Task<RecordingDisable> DisableSessionAsync(string sessionId, CancellationToken ct) {
        var session = SessionIds.Canonical(sessionId);
        var held    = _sources.Values.Where(tracked => SessionIds.Canonical(tracked.Source.SessionId) == session).ToArray();

        if (session is null) return RecordingDisable.NotHeld;
        if (held.Length == 0) return await SettledAsync(session, ct);

        // Under the lock a source starts under, so each of these is running by now or never will be.
        lock (_starting) {
            foreach (var tracked in held) tracked.Disabled = true;
        }

        var silenced = _host.IsValueCreated ? _transport!.SilenceAsync(session) : Task.FromResult(true);
        var ended    = new List<Task> { silenced };

        foreach (var tracked in held) {
            if (tracked.Running) {
                ended.Add(_host.Value.AbandonAsync(tracked.Source.Key));
                _claims.Release(tracked.Source.Key);
                _list.Remove(tracked.Source.Key);
            } else {
                Forget(tracked);
            }
        }

        DiscardSpool(session);

        try {
            await Task.WhenAll(ended).WaitAsync(DisableBound, _time, ct);

            return await silenced ? RecordingDisable.Stopped : RecordingDisable.SendInFlight;
        } catch (TimeoutException) {
            return ended.All(task => task == silenced || task.IsCompleted) ? RecordingDisable.SendInFlight : RecordingDisable.StillStopping;
        }
    }

    /// <summary>A later run of the command, with nothing left here to stop: what an earlier one left on its way has had its
    /// answer by the time this answers <see cref="RecordingDisable.NotHeld"/>, so the deletion after it covers it. One that
    /// lost its answer stays a send that may have landed.</summary>
    async Task<RecordingDisable> SettledAsync(string session, CancellationToken ct) {
        if (!_host.IsValueCreated) return RecordingDisable.NotHeld;

        try {
            // The transport keeps the session's silence, so a send that lost its answer is still known as one.
            return await _transport!.SilenceAsync(session).WaitAsync(DisableBound, _time, ct) ? RecordingDisable.NotHeld : RecordingDisable.SendInFlight;
        } catch (TimeoutException) {
            return RecordingDisable.SendInFlight;
        }
    }

    /// <summary>Stops every source for a shutdown, within <see cref="ShutdownBound"/>. The next boot picks up what
    /// <paramref name="keeps"/> says is coming back, and what didn't finish: a stuck source hears no callback, so its
    /// claim and list entry stay.</summary>
    /// <param name="keeps">Whether an agent resumes under the next daemon.</param>
    public async Task StopAsync(Func<string, bool> keeps, CancellationToken ct) {
        lock (_starting) {
            Interlocked.Exchange(ref _stopping, 1);
            if (!_host.IsValueCreated) return;
        }

        await _host.Value.StopAllAsync(ShutdownBound, ct);

        var stuck = _host.Value.ActiveKeys.ToHashSet(StringComparer.Ordinal);

        foreach (var tracked in _sources.Values.Where(tracked => tracked.Running && !stuck.Contains(tracked.Source.Key))) {
            if (keeps(tracked.Source.AgentId)) {
                if (tracked.Drained) _list.TryAdd(tracked.Source with { Drained = true });
                tracked.Settled.TrySetResult();
            } else {
                Forget(tracked);
            }
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _closing.CancelAsync();

        if (_transport is IAsyncDisposable transport) await transport.DisposeAsync();

        _closing.Dispose();
    }

    RecorderHost CreateHost() {
        _transport = _connect();

        return new RecorderHost(_transport, _ => new NoStamps(), ContextFor, _time, _claims, OnAttemptEnded);
    }

    RecorderContext ContextFor(RecordingSource source) {
        var recorded = _sources[source.Key].Source;

        void Log(string message) => LogRecorder(source.Key, message);

        return new RecorderContext(
            _server, _housekeeping(recorded, Log, () => _ = FinishQuietlyAsync(source.Key, CancellationToken.None), () => _claims.Beat(source.Key)), _time, Log, static () => { });
    }

    // Under the key's gate. Tracked before the host can hear of it, because the host's first context reads it.
    async Task<Tracked?> TakeAsync(RecordedSource source, CancellationToken ct) {
        for (var attempt = 1; attempt <= TakeAttempts; attempt++) {
            switch (await TakeOnceAsync(source)) {
                case ClaimOutcome.Taken when _list.TryAdd(source): return _sources[source.Key] = new Tracked(source);
                case ClaimOutcome.Taken:
                    _claims.Release(source.Key);

                    return null;
                case ClaimOutcome.HeldByWatcher or ClaimOutcome.Disabled: return null;
            }

            if (attempt < TakeAttempts) await Task.Delay(TakeRetryGap, _time, ct);
        }

        return null;
    }

    async Task<ClaimOutcome> TakeOnceAsync(RecordedSource source) {
        if (IsDisabled(source)) return ClaimOutcome.Disabled;

        var key = source.Key;

        // What stays if the lock is never taken: a lost lock says nothing about who holds the source.
        var outcome = ClaimOutcome.Contended;

        try {
            await _liveness.WithSpawnLock(key, () => {
                // Any live pid, a wedged one's too: a claim would stop a hook from reaping it or stopping it, and it could
                // still send. One this daemon left from before goes here, while nothing can take the key in between.
                outcome = _liveness.PidAlive(key) ? ClaimOutcome.HeldByWatcher
                    : _claims.TryTake(key, _config.Name, isHosted: true) ? ClaimOutcome.Taken
                    : ClaimOutcome.Contended;

                if (outcome is ClaimOutcome.HeldByWatcher) _claims.ReleaseIfHeldBy(key, _config.Name);

                return Task.CompletedTask;
            });
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogClaimFailed(ex, key);
        }

        return outcome;
    }

    // The claim goes now, for the hooks of what is resuming. The entry stays until the settling, so a kill ahead of that
    // leaves the next boot the same decision.
    async Task LetGoAsync(RecordedSource source, RecordingScope scope, CancellationToken ct) {
        using var _ = await _keys.EnterAsync(source.Key, ct);

        if (_sources.ContainsKey(source.Key)) return;

        if (IsDisabled(source)) {
            await ForgetDisabledAsync(source, ct);

            return;
        }

        await LetGoOfOwnClaimAsync(source.Key, ct);

        if (scope.Readable) _letGo.Enqueue((source, scope.MayImport(source.Cwd)));
        else LogScopeUnreadable(source.Key);
    }

    async Task SettleLetGoAsync(RecordedSource source, bool mayImport, Func<string, bool> agentIsLive) {
        using var _ = await _keys.EnterAsync(source.Key, CancellationToken.None);

        // A hook got here first, and the entry is its recorder's.
        if (_sources.ContainsKey(source.Key)) return;

        if (IsDisabled(source)) {
            await ForgetDisabledAsync(source, CancellationToken.None);

            return;
        }

        if (mayImport && !source.Drained && !agentIsLive(source.AgentId) && !_liveness.PidAlive(source.Key)) MarkForImport(source);

        _list.Remove(source.Key);
    }

    // Nothing of it is left to pick up: no mark, which would ask for what the person deleted to be sent again.
    async Task ForgetDisabledAsync(RecordedSource source, CancellationToken ct) {
        LogDisabled(source.Key);

        await LetGoOfOwnClaimAsync(source.Key, ct);
        _list.Remove(source.Key);
        DiscardSpool(source.SessionId);
    }

    // Read and deleted under the spawn lock a take holds, or the claim deleted can be one another daemon has just taken.
    // A lock still held after the retries leaves the claim: nothing here beats it, so it goes stale and a hook takes it.
    async Task LetGoOfOwnClaimAsync(string key, CancellationToken ct) {
        for (var attempt = 1; attempt <= TakeAttempts; attempt++) {
            var locked = false;

            try {
                await _liveness.WithSpawnLock(key, () => {
                    locked = true;
                    _claims.ReleaseIfHeldBy(key, _config.Name);

                    return Task.CompletedTask;
                });
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogClaimFailed(ex, key);

                return;
            }

            if (locked) return;
            if (attempt < TakeAttempts) await Task.Delay(TakeRetryGap, _time, ct);
        }
    }

    bool IsDisabled(RecordedSource source) => DisabledSessions.IsDisabledByPerson(source.SessionId, _config.ConfigRoot);

    // Its answer is for a command waiting on it, and nobody is here.
    void Silence(string session) => _ = _transport?.SilenceAsync(session);

    void DiscardSpool(string session) =>
        new TranscriptSpool(_server.Config, _time, _server.Url) { FailureLog = message => LogRecorder(session, message) }.Discard(session);

    void MarkForImport(RecordedSource source) {
        try {
            var spool = new TranscriptSpool(_server.Config, _time, _server.Url) { FailureLog = message => LogRecorder(source.Key, message) };

            if (spool.MarkNeedsImport(source.SessionId, "its daemon stopped before the final drain, and its agent did not come back"))
                LogMarkedForImport(source.Key);
        } catch (Exception ex) {
            LogMarkFailed(ex, source.Key);
        }
    }

    // A server without WatcherDetach can't stop a source, and each would re-register on it regardless, so a definite no
    // ends them. With its claim gone a source is its hooks' again.
    async Task LetEverySourceGoAsync() {
        await Task.WhenAll(_sources.Values.ToArray().Select(tracked => tracked.Running ? FinishQuietlyAsync(tracked.Source.Key, CancellationToken.None) : LetGoUnstartedAsync(tracked)));
    }

    async Task LetGoUnstartedAsync(Tracked tracked) {
        using var _ = await _keys.EnterAsync(tracked.Source.Key, CancellationToken.None);

        if (!tracked.Running) Forget(tracked);
    }

    async Task RestoreOneAsync(RecordedSource source, CancellationToken ct) {
        using var _ = await _keys.EnterAsync(source.Key, ct);

        // A launch or a hook got here first, and this entry is that source's own.
        if (_sources.ContainsKey(source.Key)) return;

        var started = _time.GetTimestamp();

        while (true) {
            var outcome = await TakeOnceAsync(source);

            if (outcome is ClaimOutcome.Disabled) {
                await ForgetDisabledAsync(source, ct);

                return;
            }

            if (outcome is ClaimOutcome.Taken) {
                // What it sends from here is unsent again, so a kill behind this can't read as drained.
                var resumed = source with { Drained = false };
                if (source.Drained) _list.TryAdd(resumed);

                Start(_sources[source.Key] = new Tracked(resumed) { Restored = true });

                return;
            }

            if (outcome is ClaimOutcome.HeldByWatcher || _time.GetElapsedTime(started) >= RestoreWindow) {
                LogNotRestored(source.Key, outcome);

                // With no entry nothing would beat it.
                await LetGoOfOwnClaimAsync(source.Key, ct);
                _list.Remove(source.Key);

                return;
            }

            await Task.Delay(RestoreRetryGap, _time, ct);
        }
    }

    // A source past its agent's exit or its own last attempt. Its finish can't be called back, so whoever comes for its
    // key finds it gone rather than adopting something about to drop.
    static bool IsLeaving(Tracked tracked) => tracked.Ending || tracked.AgentFinished || tracked.Disabled;

    bool Start(Tracked tracked) {
        var source = tracked.Source;

        try {
            lock (_starting) {
                if (Volatile.Read(ref _stopping) == 0 && !tracked.Disabled && !IsDisabled(source) && _host.Value.TryAdd(source.Recording)) {
                    tracked.Running = true;
                    Warm(source);

                    return true;
                }
            }
        } catch (Exception ex) {
            LogStartFailed(ex, source.Key);
        }

        // Nothing will record this source, so nothing may claim it.
        Forget(tracked);

        return false;
    }

    // The transcript's backlog costs the session's first judged tool call otherwise. It never fails a recorder.
    void Warm(RecordedSource source) {
        if (source.SubagentId is not null || source.Vendor != "claude") return;

        var closing = _closing.Token;
        _ = Task.Run(() => ClaudeJudgeWarmup.RunAsync(_config.ConfigRoot, source.SessionId, source.TranscriptPath, source.Cwd, _time, closing));
    }

    async Task FinishQuietlyAsync(string key, CancellationToken ct) {
        try {
            await FinishAsync(key, ct);
        } catch (OperationCanceledException) {
            // The bound passed, and the drain carries on.
        } catch (Exception ex) {
            LogFinishFailed(ex, key);
        }
    }

    // On the source's own loop, so nothing here waits for the source. A failed attempt is retried, and nothing is decided.
    internal void OnAttemptEnded(RecorderAttemptEnded ended) {
        if (!ended.SourceEnded || !_sources.TryGetValue(ended.Source.Key, out var tracked)) return;

        if (ended is { Failure: null, LostClaim: false, LeftLines: false }) tracked.Drained = true;

        if (Volatile.Read(ref _stopping) != 0 && !ended.LostClaim) return;

        tracked.Ending = true;
        _ = Task.Run(() => SettleAsync(tracked, ended.LostClaim));
    }

    // The host's finish returns once the key has left, and the gate keeps anyone from adding it back before the claim goes.
    async Task SettleAsync(Tracked tracked, bool lostClaim) {
        var key = tracked.Source.Key;

        try {
            using var _ = await _keys.EnterAsync(key, CancellationToken.None);

            await _host.Value.FinishAsync(key, CancellationToken.None);

            // A stuck source's last moves come long after the disable that stopped it. With no frame behind it, from a kcap
            // that knows none, the marker is all a recorder heard, and what the frame would have cleared is still here.
            if (tracked.Disabled || IsDisabled(tracked.Source)) {
                Silence(tracked.Source.SessionId);
                await ForgetDisabledAsync(tracked.Source, CancellationToken.None);
            }

            if (lostClaim) {
                _list.Remove(key);
                _sources.TryRemove(key, out var _);
                tracked.Settled.TrySetResult();
            } else {
                Forget(tracked);
            }
        } catch (Exception ex) {
            LogFinishFailed(ex, key);
            tracked.Settled.TrySetResult();
        }
    }

    void Forget(Tracked tracked) {
        var key = tracked.Source.Key;

        _sources.TryRemove(key, out var _);
        _claims.Release(key);
        _list.Remove(key);
        tracked.Settled.TrySetResult();
    }

    /// <summary>A recorder's git and gh run on this process's PATH, which is the user's terminal PATH on a Mac and the
    /// service unit's elsewhere. One that can't start reads as a session with no repository, so it is said here once.</summary>
    static void WarnOfMissingTools(DaemonConfig config, ILogger logger) {
        foreach (var tool in new[] { "git", "gh" })
            if (!config.Binaries.Finds(tool)) LogToolMissing(logger, tool);
    }

    /// <summary>Finishes a title through the CLI beside this binary: a recorder's process path would name the daemon.</summary>
    static void HandOffTitle(DaemonConfig config, ILogger logger, string sessionId, string vendor, string transcriptPath) {
        try {
            var psi = new ProcessStartInfo(config.KcapCliPath ?? config.CapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = config.ServerUrl,
                    [ConfigRoot.ConfigDirEnvVar] = config.ConfigRoot.Directory,
                },
            };
            psi.ArgumentList.Add(TitleGeneration.HandOffCommand);
            psi.ArgumentList.Add(sessionId);
            psi.ArgumentList.Add(vendor);
            psi.ArgumentList.Add(transcriptPath);

            using var process = Process.Start(WindowsLaunchCommand.Apply(psi, config.Binaries));

            if (process is null) {
                LogTitleHandOffFailed(logger, null, sessionId);

                return;
            }

            // It runs to completion by itself, and the pipes must not stay open for its lifetime.
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        } catch (Exception ex) {
            LogTitleHandOffFailed(logger, ex, sessionId);
        }
    }

    enum ClaimOutcome { Contended, Taken, HeldByWatcher, Disabled }

    sealed class Tracked(RecordedSource source) {
        public RecordedSource Source { get; set; } = source;

        /// <summary>The host has it. False from the claim to the agent's start.</summary>
        public bool Running { get; set; }

        /// <summary>The last daemon left it, and no agent of this daemon has taken it.</summary>
        public bool Restored { get; set; }

        /// <summary>Told apart from a source still going, for whoever arrives between its last attempt and the settling.</summary>
        public bool Ending { get; set; }

        /// <summary>Its agent has exited and its finish has been asked for.</summary>
        public bool AgentFinished { get; set; }

        /// <summary>A person disabled its session, so it is over, whatever else it says here.</summary>
        public bool Disabled { get; set; }

        /// <summary>Its final drain ended with no failure and nothing left behind.</summary>
        public bool Drained { get; set; }

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Key}: {Message}")]
    partial void LogRecorder(string key, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} is not on this daemon's PATH, so recorded sessions report no repository or pull request through it")]
    static partial void LogToolMissing(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "The transcript of {Key} is at {Path}; moving its recorder there")]
    partial void LogTranscriptMoved(string key, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Taking the claim on {Key} failed")]
    partial void LogClaimFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} recorded sources are waiting: the server has not said whether it can stop one")]
    partial void LogRestoreUnanswered(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Picking up what the last daemon left failed once the server answered")]
    partial void LogLateRestoreFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Key} is left for the next boot: the capture scope can't be read, so nothing can say whether it may be imported")]
    partial void LogScopeUnreadable(string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "No claim on {Key} ahead of its launch: something else has its key, and its hook decides")]
    partial void LogClaimGaveUp(string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not picking up {Key}: {Outcome}")]
    partial void LogNotRestored(string key, ClaimOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not recording {Key}: its session was disabled")]
    partial void LogDisabled(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Key} may have lines the server never got, and is marked for import")]
    partial void LogMarkedForImport(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marking {Key} for import failed")]
    partial void LogMarkFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Key} could not start")]
    partial void LogStartFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finishing the recording of {Key} failed")]
    partial void LogFinishFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handing the title of {SessionId} to the CLI failed")]
    static partial void LogTitleHandOffFailed(ILogger logger, Exception? ex, string sessionId);
}
