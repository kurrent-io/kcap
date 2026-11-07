using System.Collections.Concurrent;
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording;
using Capacitor.Cli.Recording.Harness.Claude;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Records this daemon's hosted Claude sessions in its own process, so a hosted session needs no watcher. Off until
/// <see cref="DaemonConfig.RecordHostedClaude"/> is set and the server is at <see cref="RecordingServerFloor"/>; while
/// off it opens no connection and takes no claim.
///
/// <para>A source's life is one claim and one list entry around one <see cref="RecorderHost"/> entry. Its claim is taken
/// under the spawn lock with the live-watcher check inside it, before the list entry and before the host hears of it,
/// because a host adding a source with no claim reads it as lost and silently records nothing. A source that finished
/// lets its claim go, and only once its key has left the host: a take behind a release while the key is still held is
/// refused by the host and leaves the claim with nothing beating it. A source that lost its claim touches it no more,
/// since it is someone else's. A shutdown lets go only of what no agent comes back for: the next boot picks up the rest.</para>
/// </summary>
internal sealed partial class DaemonRecorder : IAsyncDisposable {
    // A lost spawn lock is another process in the same few file operations, and a launch waits behind these.
    const int TakeAttempts = 4;
    static readonly TimeSpan TakeRetryGap = TimeSpan.FromMilliseconds(50);

    // A fresh claim that isn't this daemon's goes stale within the grace if its holder is dead.
    static readonly TimeSpan RestoreRetryGap = TimeSpan.FromSeconds(1);
    static readonly TimeSpan RestoreWindow   = RecorderClaims.Grace + TimeSpan.FromSeconds(5);

    /// <summary>Beside the shutdown's report to the server and no longer than it, so it adds nothing to a shutdown. Of
    /// this, 2 seconds stay back for the spool, which is what a host keeps of any bound from 4 seconds up.</summary>
    internal static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(5);

    readonly DaemonConfig _config;
    readonly CapacitorServer _server;
    readonly Func<IRecorderTransport> _connect;
    readonly Func<RecordedSource, Action<string>, Action, IRecorderHousekeeping> _housekeeping;
    readonly TimeProvider _time;
    readonly ILogger _logger;
    readonly RecorderClaims _claims;
    readonly WatcherLiveness _liveness;
    readonly RecordedSourceList _list;
    readonly KeyedAsyncGate _keys = new();
    readonly ConcurrentDictionary<string, Tracked> _sources = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _closing = new();
    readonly Lazy<RecorderHost> _host;

    IRecorderTransport? _transport;
    int _stopping;
    int _disposed;

    /// <param name="connect">Builds the transport, for the first source. Nothing connects before that.</param>
    /// <param name="housekeeping">Builds a recorder's housekeeping, given its source, its log and its own stop.</param>
    public DaemonRecorder(
            DaemonConfig config, CapacitorServer server, Func<IRecorderTransport> connect,
            Func<RecordedSource, Action<string>, Action, IRecorderHousekeeping> housekeeping, TimeProvider time, ILogger<DaemonRecorder> logger) {
        _config       = config;
        _server       = server;
        _connect      = connect;
        _housekeeping = housekeeping;
        _time         = time;
        _logger       = logger;

        var paths = WatcherPaths.FromEnvironment(config.ConfigRoot);
        _claims   = new RecorderClaims(paths.Directory, time);
        _liveness = new WatcherLiveness(paths, time);
        _list     = new RecordedSourceList(config.Store.StateDirectory(config.Name), logger);
        _host     = new Lazy<RecorderHost>(CreateHost);
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
            config, server,
            () => {
                WarnOfMissingTools(config, logger);

                return new ServerRecorderTransport(
                    DaemonHub.Build(config, async () => (await connection.Credentials.ResolveAsync(default)).Bearer,
                        report => logger.LogWarning("Server refused the recording connection's bearer — {Report}", report), loggers, time),
                    time, message => logger.LogInformation("{Message}", message));
            },
            (source, log, stop) => new RecorderHousekeeping(
                source.Recording, source.SkipTitle, parentPid: null,
                new DaemonRecorderHost(config.ConfigRoot, hookCommand, time, stop, (session, vendor, path) => HandOffTitle(config, logger, session, vendor, path), log),
                config.ConfigRoot, config.Profiles, harnesses, http, router, time, accounts, config.Home, log, static () => { }),
            time, logger);
    }

    /// <summary>Whether this daemon records hosted Claude now. Read on every use: the server can move past the floor, or
    /// back under it, and a hook takes a source to a daemon only while this holds.</summary>
    public bool Enabled => _config.RecordHostedClaude && RecordingServerFloor.Met(ServerVersionStore.Get(_config.ServerUrl, _config.ConfigRoot));

    /// <summary>The keys being recorded now.</summary>
    internal IReadOnlyCollection<string> ActiveKeys => _host.IsValueCreated ? _host.Value.ActiveKeys : [];

    /// <summary>Takes <paramref name="source"/>'s claim ahead of its agent process, so the process's first hook finds it.
    /// True when the claim is this daemon's; false leaves the source to whatever a hook starts. A caller with a true
    /// answer follows with <see cref="StartForAgent"/>, or <see cref="ReleaseClaim"/> if the agent never ran.</summary>
    public async Task<bool> ClaimAsync(RecordedSource source, CancellationToken ct) {
        using var _ = await _keys.EnterAsync(source.Key, ct);

        return _sources.ContainsKey(source.Key) || await TakeAsync(source, ct) is not null;
    }

    /// <summary>Starts recording a source <see cref="ClaimAsync"/> claimed, with its agent published. A source the last
    /// daemon left is already being recorded, and is now this agent's.</summary>
    public void StartForAgent(RecordedSource source) {
        if (!_sources.TryGetValue(source.Key, out var tracked)) return;

        if (tracked.Running) {
            tracked.Source   = source;
            tracked.Restored = false;
            _list.TryAdd(source);

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

                if (!held.Ending) return true;
            }

            await held.Settled.Task.WaitAsync(ct);
        }
    }

    /// <summary>Moves a source to where its transcript turned out to be. Claude's project directory is computed ahead of
    /// the launch, and a path it spells differently would otherwise be tailed for ever with the session unrecorded.</summary>
    public async Task ObserveTranscriptAsync(RecordedSource found) {
        if (!_sources.TryGetValue(found.Key, out var tracked) || tracked.Source.TranscriptPath == found.TranscriptPath) return;
        if (File.Exists(tracked.Source.TranscriptPath)) return;

        LogTranscriptMoved(found.Key, found.TranscriptPath);
        await FinishAsync(found.Key, CancellationToken.None);
        await TryRecordAsync(found with { SkipTitle = tracked.Source.SkipTitle }, CancellationToken.None);
    }

    /// <summary>Makes the final drain of the source <paramref name="key"/> names and lets it go. Off the caller's thread,
    /// because a cancel runs the drain up to its first real wait, and <paramref name="ct"/> bounds only the waiting: the
    /// drain carries on without it.</summary>
    /// <returns>Whether this daemon held the source and it finished.</returns>
    /// <param name="hurried">The caller won't wait long, as a hook doesn't.</param>
    public async Task<bool> FinishAsync(string key, CancellationToken ct, bool hurried = false) {
        if (Volatile.Read(ref _stopping) != 0 || !_sources.TryGetValue(key, out var tracked) || !tracked.Running) return false;

        await Task.Run(() => _host.Value.FinishAsync(key, ct, hurried), CancellationToken.None);
        await tracked.Settled.Task.WaitAsync(ct);

        return true;
    }

    /// <summary>Finishes every source of an exited agent, within <paramref name="budget"/>, once: a second call on the
    /// way through a teardown spends none of it on a drain already asked for. Never throws, and what the budget cuts off
    /// carries on: a tail landing behind the session's end reactivates it, and the server's sweep closes it again once
    /// the source has detached.</summary>
    public async Task FinishAgentAsync(string agentId, TimeSpan budget) {
        var keys = _sources.Where(source => source.Value.Source.AgentId == agentId && !source.Value.AgentFinished).Select(source => source.Key).ToArray();
        if (keys.Length == 0) return;

        foreach (var key in keys) {
            if (_sources.TryGetValue(key, out var tracked)) tracked.AgentFinished = true;
        }

        using var bound = new CancellationTokenSource(budget, _time);

        await Task.WhenAll(keys.Select(key => FinishQuietlyAsync(key, bound.Token)));
    }

    /// <summary>Picks up what the last daemon left: each source from the line the server answers with, unless a live
    /// watcher holds it now. With recording off, it lets all of it go, because a claim nobody is coming back for would
    /// keep a resumed session's hooks from starting a watcher.</summary>
    public async Task RestoreAsync(CancellationToken ct) {
        var enabled = Enabled;

        if (_config.RecordHostedClaude && !enabled)
            LogBelowFloor(ServerVersionStore.Get(_config.ServerUrl, _config.ConfigRoot) ?? "unknown", RecordingServerFloor.MinimumVersion);

        var left = _list.Load();
        if (left.Count == 0) return;

        if (!enabled) {
            foreach (var source in left) {
                _claims.Release(source.Key);
                _list.Remove(source.Key);
            }

            return;
        }

        await Task.WhenAll(left.Select(source => RestoreOneAsync(source, ct)));
    }

    /// <summary>Finishes each restored source whose agent didn't come back, once the boot's resumes have run.</summary>
    public void SettleRestored(Func<string, bool> agentIsLive) {
        foreach (var tracked in _sources.Values.Where(source => source.Restored && !agentIsLive(source.Source.AgentId)))
            _ = FinishQuietlyAsync(tracked.Source.Key, CancellationToken.None);
    }

    /// <summary>Stops every source for a shutdown, within <see cref="ShutdownBound"/>. The next boot picks up what
    /// <paramref name="keeps"/> says is coming back, and what didn't finish: a stuck source hears no callback, so its
    /// claim and list entry stay.</summary>
    /// <param name="keeps">Whether an agent resumes under the next daemon.</param>
    public async Task StopAsync(Func<string, bool> keeps, CancellationToken ct) {
        Interlocked.Exchange(ref _stopping, 1);
        if (!_host.IsValueCreated) return;

        await _host.Value.StopAllAsync(ShutdownBound, ct);

        var stuck = _host.Value.ActiveKeys.ToHashSet(StringComparer.Ordinal);

        foreach (var tracked in _sources.Values) {
            if (stuck.Contains(tracked.Source.Key) || !tracked.Running) continue;

            if (keeps(tracked.Source.AgentId)) tracked.Settled.TrySetResult();
            else Forget(tracked);
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
            _server, _housekeeping(recorded, Log, () => _ = FinishQuietlyAsync(source.Key, CancellationToken.None)), _time, Log, static () => { });
    }

    // Under the key's gate. Tracked before the host can hear of it, because the host's first context reads it.
    async Task<Tracked?> TakeAsync(RecordedSource source, CancellationToken ct) {
        for (var attempt = 1; attempt <= TakeAttempts; attempt++) {
            switch (await TakeOnceAsync(source.Key)) {
                case ClaimOutcome.Taken when _list.TryAdd(source): return _sources[source.Key] = new Tracked(source);
                case ClaimOutcome.Taken:
                    _claims.Release(source.Key);

                    return null;
                case ClaimOutcome.HeldByWatcher: return null;
            }

            if (attempt < TakeAttempts) await Task.Delay(TakeRetryGap, _time, ct);
        }

        return null;
    }

    async Task<ClaimOutcome> TakeOnceAsync(string key) {
        // What stays if the lock is never taken: a lost lock says nothing about who holds the source.
        var outcome = ClaimOutcome.Contended;

        try {
            await _liveness.WithSpawnLock(key, () => {
                // Any live pid, a wedged one's too: a claim would stop a hook from reaping it, and it could still send.
                outcome = _liveness.PidAlive(key) ? ClaimOutcome.HeldByWatcher
                    : _claims.TryTake(key, _config.Name, isHosted: true) ? ClaimOutcome.Taken
                    : ClaimOutcome.Contended;

                return Task.CompletedTask;
            });
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogClaimFailed(ex, key);
        }

        return outcome;
    }

    async Task RestoreOneAsync(RecordedSource source, CancellationToken ct) {
        using var _ = await _keys.EnterAsync(source.Key, ct);

        var started = _time.GetTimestamp();

        while (true) {
            var outcome = await TakeOnceAsync(source.Key);

            if (outcome is ClaimOutcome.Taken) {
                Start(_sources[source.Key] = new Tracked(source) { Restored = true });

                return;
            }

            if (outcome is ClaimOutcome.HeldByWatcher || _time.GetElapsedTime(started) >= RestoreWindow) {
                LogNotRestored(source.Key, outcome);

                // A claim left beside a live watcher would keep every hook from stopping it.
                if (outcome is ClaimOutcome.HeldByWatcher) _claims.Release(source.Key);
                _list.Remove(source.Key);

                return;
            }

            await Task.Delay(RestoreRetryGap, _time, ct);
        }
    }

    bool Start(Tracked tracked) {
        var source = tracked.Source;

        try {
            if (_host.Value.TryAdd(source.Recording)) {
                tracked.Running = true;
                Warm(source);

                return true;
            }
        } catch (Exception ex) {
            LogStartFailed(ex, source.Key);
        }

        // Refused only while stopping, or the key would be running: nothing will record this source, so nothing may claim it.
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

    enum ClaimOutcome { Contended, Taken, HeldByWatcher }

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

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Key}: {Message}")]
    partial void LogRecorder(string key, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording is on, but the server's version is {Version} and it needs {Floor}: hosted sessions stay on their watchers")]
    partial void LogBelowFloor(string version, string floor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} is not on this daemon's PATH, so recorded sessions report no repository or pull request through it")]
    static partial void LogToolMissing(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "The transcript of {Key} is at {Path}; moving its recorder there")]
    partial void LogTranscriptMoved(string key, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Taking the claim on {Key} failed")]
    partial void LogClaimFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not picking up {Key}: {Outcome}")]
    partial void LogNotRestored(string key, ClaimOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Key} could not start")]
    partial void LogStartFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finishing the recording of {Key} failed")]
    partial void LogFinishFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handing the title of {SessionId} to the CLI failed")]
    static partial void LogTitleHandOffFailed(ILogger logger, Exception? ex, string sessionId);
}
