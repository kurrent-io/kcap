using Capacitor.Cli.Recording;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Recording.Capture;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli;

public sealed partial class WatcherManager(
        ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, IProcessStarter starter,
        WatcherPaths paths, IWatcherSpawner spawner, TimeProvider time, IDaemonRecordingClient? daemonRecording = null, HostedAgent? hosted = null) {
    // As long as a hook waits for a watcher to exit after asking it to stop.
    static readonly TimeSpan FinishSourceBudget = TimeSpan.FromSeconds(5);

    // Two local round trips and a claim. A hook past this starts a watcher, and the spawn lock settles who records.
    internal static readonly TimeSpan HandOffBudget = TimeSpan.FromSeconds(2);

    // Past the daemon's own bound, so its answer that a send is still out arrives rather than reading as no answer. With no
    // claim nothing turns on the answer, so that case keeps to HandOffBudget.
    internal static readonly TimeSpan DisableBudget = TimeSpan.FromSeconds(8);

    readonly IDaemonRecordingClient _daemonRecording = daemonRecording ?? NoDaemonRecording.Instance;
    readonly HostedAgent            _hosted          = hosted ?? HostedAgent.FromEnvironment();
    readonly RecorderClaims         _claims          = new(paths.Directory, time);
    readonly WatcherLiveness        _liveness        = new(paths, time);

    // The one URL this process resolved. No member takes one: a watcher spawned against a different
    // server than the hook that spawned it would stream a session nothing on this side can see.
    // Nullable because an offline invocation resolves none — the IsPostable guards refuse that.
    string? Url => profiles.Resolution.ServerUrl;

    /// <summary>Replaces the redaction a drain runs, for a test that stalls it.</summary>
    internal Func<InlineDrainRedaction>? DrainRedaction { get; init; }

    /// <summary>Replaces the commit observation a drain starts from, for a test with no git hook to read.</summary>
    internal CommitObservation? DrainCommits { get; init; }

    internal string GetWatcherDir() => paths.Directory;

    internal string GetHeartbeatFilePath(string key) => paths.HeartbeatFile(key);


    /// <summary>
    /// Deletes the per-key heartbeat + started markers so they don't leak
    /// per-session the way the pid file never did. Deliberately does NOT touch the
    /// <c>{key}.spawnlock</c> file: <see cref="KillWatcher"/> can run from inside
    /// <see cref="WatcherLiveness.WithSpawnLock"/> (the wedged-watcher reap path), and unlinking a held lock
    /// file on POSIX lets a racing hook open a fresh inode with a non-conflicting flock —
    /// reopening the double-spawn hole (the same reason <c>DaemonLock</c> never unlinks its
    /// lock file). The spawn lock is swept by <see cref="PurgeAuxiliaryFiles"/> / <c>kcap cleanup</c>.
    /// </summary>
    void DeleteHeartbeatFiles(string key) {
        try { File.Delete(paths.HeartbeatFile(key)); } catch { /* best-effort */ }
        try { File.Delete(paths.StartedFile(key)); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Removes every leftover per-key auxiliary file (<c>*.heartbeat</c>/<c>*.started</c>/
    /// <c>*.spawnlock</c>) in the watcher directory. Called by <c>kcap cleanup</c> after all
    /// watchers are killed. A spawn lock goes only if it can be taken, since another process can hold
    /// one while cleanup runs; a held one waits for the next cleanup. Returns the number of files removed.
    /// </summary>
    public int PurgeAuxiliaryFiles() {
        var dir = paths.Directory;

        if (!Directory.Exists(dir)) {
            return 0;
        }

        var removed = 0;

        foreach (var pattern in new[] { "*.heartbeat", "*.started" }) {
            foreach (var file in Directory.GetFiles(dir, pattern)) {
                try {
                    File.Delete(file);
                    removed++;
                } catch {
                    /* best-effort */
                }
            }
        }

        removed += Directory.GetFiles(dir, "*.spawnlock").Count(WatcherLiveness.TryDeleteSpawnLock);

        return removed;
    }

    /// <summary>
    /// Kills the watcher process for the given key. Returns true if the watcher was running and was killed,
    /// false if it was already dead, no PID file existed, or the kill failed. The pid file and the
    /// heartbeat/started markers go only once no watcher can be left running, so a failed kill leaves
    /// them for a retry to find; see <see cref="DeleteHeartbeatFiles"/> for why the spawn lock is
    /// intentionally left behind here. A claim on <paramref name="key"/>, stale or not, means a daemon
    /// is the recorder, and this never signals a daemon: all it does then is clear a dead watcher's files.
    /// </summary>
    public async Task<bool> KillWatcher(string key, TextWriter? stderr = null) {
        var pidFile = paths.PidFile(key);

        if (_claims.Read(key) is not RecorderClaimState.None) {
            if (File.Exists(pidFile)) {
                await _liveness.WithSpawnLock(key, () => {
                    SweepDeadWatcher(key);

                    return Task.CompletedTask;
                });
            }

            return false;
        }

        if (!File.Exists(pidFile)) {
            // No live watcher, but sweep any orphaned heartbeat/started markers for this key.
            DeleteHeartbeatFiles(key);

            return false;
        }

        var (outcome, pid) = await KillProcess(key, pidFile, stderr ?? Console.Error);
        if (outcome is not { } killed) return false;

        // A watcher asked to stop can arm a successor under the same key before it exits; its files stay.
        if (pid is { } stopped) {
            RemoveOwnPidFile(key, stopped);
        } else {
            try { File.Delete(pidFile); } catch {
                /* ignore */
            }

            DeleteHeartbeatFiles(key);
        }

        return killed;
    }

    /// <summary>Stops everything recording a session a person disabled, its subagents' sources included. Each watcher
    /// stops as <see cref="KillWatcher"/> stops one, and one whose pid file outlives that is still running. A daemon's
    /// sources are found by their claims, because it leaves no pid file, and it is asked to stop rather than signalled.
    /// Every daemon on the machine is asked, whatever its profile: only a daemon can say a live agent's output goes on, and
    /// neither a hosted session on a watcher nor an ACP session has a claim. A drain of what hooks spooled that is already
    /// under way is waited out, within <see cref="SpoolPassBound"/>, and a post of the session one sent with no answer
    /// leaves the stop unconfirmed.</summary>
    public async Task<RecordingStopReport> StopRecordingAsync(string sessionId, TextWriter? stderr = null) {
        var report = await StopDaemonsAsync(sessionId);

        // Ahead of the listing, which the session's own watcher needs none of.
        try {
            if (!await TryStopWatcher(sessionId, stderr)) report = report with { WatcherLeft = true };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            report = report with { Unreadable = true };
        }

        try {
            var subagents = Directory.Exists(paths.Directory) ? Directory.GetFiles(paths.Directory, $"{sessionId}-*.pid").Select(Path.GetFileNameWithoutExtension) : [];

            foreach (var key in subagents.ToList()) {
                if (!await TryStopWatcher(key!, stderr)) report = report with { WatcherLeft = true };
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            report = report with { Unreadable = true };
        }

        return await SpoolPostsSettledAsync(sessionId) ? report : report with { DrainLeft = true };
    }

    // Past the 3 s a drain gives each post, so a post a pass sent ahead of the marker has had its answer by then.
    internal static readonly TimeSpan SpoolPassBound = TimeSpan.FromSeconds(4);

    static readonly TimeSpan SpoolLeasePoll = TimeSpan.FromMilliseconds(50);

    string[] SpoolDirectories => [new HookSpool(config, time, Url).DirectoryPath, new WorkspaceSpool(config, Url, time).DirectoryPath];

    /// <summary>Whether every post of the session this profile's spool drains sent has had its answer. A pass holds its
    /// spool's lease throughout and reads the marker before each post, so one that starts later sends nothing of the
    /// session, while one under way may have read it a moment before it went down. A free lease says only that the pass
    /// has ended: a post it heard nothing back for is in <see cref="UnansweredPosts"/>.</summary>
    async Task<bool> SpoolPostsSettledAsync(string sessionId) {
        using var bound = new CancellationTokenSource(SpoolPassBound, time);

        var settled = true;

        foreach (var dir in SpoolDirectories) {
            // A pass under way made its directory.
            if (!Directory.Exists(dir)) continue;

            SpoolDrainLease? lease;

            while ((lease = SpoolDrainLease.TryTake(dir)) is null) {
                try {
                    await Task.Delay(SpoolLeasePoll, time, bound.Token);
                } catch (OperationCanceledException) {
                    return false;
                }
            }

            using (lease) {
                if (UnansweredPosts.Has(dir, sessionId)) settled = false;
            }
        }

        return settled;
    }

    /// <summary>Drops the session's record of posts with no answer, once a deletion has run behind them: from there what
    /// they could still bring back is the deletion's own doubt, which the disable keeps.</summary>
    public void ForgetUnansweredPosts(string sessionId) {
        foreach (var dir in SpoolDirectories) UnansweredPosts.Clear(dir, sessionId);
    }

    /// <summary>A daemon asked in a disable, and the claim that ties it to the session, if one does.</summary>
    sealed record AskedDaemon(string? Name, bool Held, bool Fresh);

    async Task<RecordingStopReport> StopDaemonsAsync(string sessionId) {
        var keys   = _claims.KeysOf(sessionId);
        var claims = (keys ?? []).Select(key => (Key: key, Holder: _claims.Holder(key), Fresh: _claims.Read(key) is RecorderClaimState.DaemonFresh)).ToArray();
        var known  = _daemonRecording.Daemons();

        var asked = claims.GroupBy(claim => claim.Holder).Select(holder => new AskedDaemon(holder.Key, true, holder.Any(claim => claim.Fresh))).ToList();

        foreach (var name in (known ?? []).Prepend(DaemonNameResolver.Resolve([], profiles.DaemonName)).Distinct()) {
            if (asked.All(daemon => daemon.Name != name)) asked.Add(new AskedDaemon(name, Held: false, Fresh: false));
        }

        // A daemon finishing a stop needs longer than one that holds nothing, and a person waits on the slowest.
        using var budget = new CancellationTokenSource(claims.Length > 0 || keys is null ? DisableBudget : HandOffBudget, time);

        var answers = await Task.WhenAll(asked.Select(async daemon => (
            Daemon: daemon,
            Answer: daemon.Name is { } name ? await AskToDisableAsync(name, sessionId, budget.Token) : new RecordingDisableAnswer(RecordingDisable.Silent))));

        // Whatever was said: a recorder with no claim stops at its next tick, a daemon's that can't answer included.
        foreach (var claim in claims) _claims.Release(claim.Key);

        var otherServers = new List<string>();

        foreach (var (daemon, answer) in answers) {
            if (HadTheSession(daemon, answer) && answer.Server is { } server && IsAnotherServer(server) && !otherServers.Any(known => DisabledSessions.SameServer(known, server)))
                otherServers.Add(server);
        }

        return new RecordingStopReport(
            OutcomeOf(answers.Select(answer => (answer.Daemon, answer.Answer.Outcome)).ToList()),
            otherServers,
            AgentLive: answers.Any(answer => answer.Answer.AgentLive),
            Unreadable: keys is null || known is null,
            DaemonUnmarked: answers.Any(answer => answer.Answer.MarkFailed));
    }

    // A claim, a stop it made or an agent it hosts: a daemon under another config root has no claim here, and its answer is
    // all that says the session reached its server.
    static bool HadTheSession(AskedDaemon daemon, RecordingDisableAnswer answer) =>
        daemon.Held || answer.AgentLive || answer.Outcome is RecordingDisable.Stopped or RecordingDisable.SendInFlight or RecordingDisable.StillStopping;

    // A daemon a claim names that hasn't answered may be recording the session. Any other with no socket has nothing to
    // record with, and a stale claim nobody answers for is a daemon that has gone.
    static RecordingStopOutcome OutcomeOf(IReadOnlyList<(AskedDaemon Daemon, RecordingDisable Outcome)> answers) =>
        answers.Any(answer => answer.Outcome is RecordingDisable.Silent ? answer.Daemon.Held : answer.Outcome is RecordingDisable.NoSocket && answer.Daemon.Fresh)
            ? RecordingStopOutcome.HolderUnconfirmed
      : answers.Any(answer => answer.Outcome is RecordingDisable.Silent) ? RecordingStopOutcome.DaemonSilent
      : answers.Any(answer => answer.Outcome is RecordingDisable.StillStopping) ? RecordingStopOutcome.StillStopping
      : answers.Any(answer => answer.Outcome is RecordingDisable.SendInFlight) ? RecordingStopOutcome.SendInFlight
      : RecordingStopOutcome.Stopped;

    bool IsAnotherServer(string? server) => server is not null && ServerIdentity.Canonicalize(server) != ServerIdentity.Canonicalize(Url);

    async Task<RecordingDisableAnswer> AskToDisableAsync(string daemonName, string sessionId, CancellationToken ct) {
        try {
            return await _daemonRecording.DisableSessionAsync(daemonName, sessionId, ct).WaitAsync(ct);
        } catch {
            return new RecordingDisableAnswer(RecordingDisable.Silent);
        }
    }

    /// <summary>Clears what a dead watcher left beside a claim, signalling nothing: its pid file would otherwise read
    /// as a watcher that could not be stopped. Runs under the spawn lock, because a hook that has just taken the claim
    /// writes a new pid file there.</summary>
    void SweepDeadWatcher(string key) {
        if (PidAlive(key)) return;

        try { File.Delete(paths.PidFile(key)); } catch { /* best-effort */ }

        DeleteHeartbeatFiles(key);
    }

    /// <summary>True once no watcher for <paramref name="key"/> can still be running: killed, or gone
    /// already. False keeps its pid file, so calling again retries the kill.</summary>
    public async Task<bool> TryStopWatcher(string key, TextWriter? stderr = null) {
        await KillWatcher(key, stderr);

        return !File.Exists(paths.PidFile(key));
    }

    /// <summary>True when killed, false when the watcher is conclusively gone, null when it may still be running;
    /// with the pid the file named, when it named one.</summary>
    async Task<(bool? Killed, int? Pid)> KillProcess(string key, string pidFile, TextWriter stderr) {
        int? pid   = null;
        var  token = "";

        try {
            // Line 1 is the pid; line 2 (when present) is the incarnation's ProcessStartToken
            // written by the spawner — the same layout as the daemon pid file.
            var lines = await File.ReadAllLinesAsync(pidFile);

            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var parsed)) {
                return (false, null);
            }

            pid   = parsed;
            token = lines.Length > 1 ? lines[1].Trim() : "";

            // "Ambiguity never kills" (ProcessStartToken): a conclusive token mismatch means the
            // watcher died and the OS recycled its pid onto an unrelated process — sweep the
            // stale file, never signal. A missing/uncomparable token falls through to the kill.
            if (token.Length > 0 && ProcessStartToken.Matches(parsed, token) == false) {
                await stderr.WriteLineAsync($"Watcher {key} (PID {parsed}) was recycled by another process; sweeping stale pid file");

                return (false, parsed);
            }

            try {
                using var process = Process.GetProcessById(parsed);

                // Ask first so the watcher runs its shutdown path (final drain, tail spool): SIGTERM
                // on Unix, the stop event on Windows. Process.Kill skips all of it.
                if (!TrySignalTerm(parsed) && !WatcherStopSignal.TryRequest(parsed)) {
                    process.Kill(entireProcessTree: false);
                }

                // Wait up to 5 seconds for graceful exit
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);

                try {
                    await process.WaitForExitAsync(cts.Token);
                    await stderr.WriteLineAsync($"Watcher {key} (PID {parsed}) exited gracefully");
                } catch (OperationCanceledException) {
                    // Force kill if it didn't exit in time
                    process.Kill(entireProcessTree: true);
                    await stderr.WriteLineAsync($"Watcher {key} (PID {parsed}) force-killed after timeout");
                }

                return (true, parsed);
            } catch (ArgumentException) {
                await stderr.WriteLineAsync($"Watcher {key} (PID {parsed}) already exited");

                return (false, parsed);
            }
        } catch (Exception ex) {
            if (pid is { } known && WatcherLiveness.IsGone(known, token)) {
                await stderr.WriteLineAsync($"Watcher {key} (PID {known}) exited while being killed");

                return (false, known);
            }

            await stderr.WriteLineAsync($"Error killing watcher {key}: {ex.Message}; keeping its pid file");

            return (null, pid);
        }
    }

    /// <summary>
    /// Stops every watcher in <paramref name="keys"/> concurrently (#550, a session watcher's
    /// teardown of its spawned children). <see cref="KillWatcher"/>'s SIGTERM-first gives each
    /// child its final drain; its 5s force-kill bound keeps a wedged child from stalling the caller.
    /// </summary>
    public Task KillWatchers(IEnumerable<string> keys) =>
        Task.WhenAll(keys.Select(key => KillWatcher(key)));

    /// <summary>
    /// Retires this watcher's own pid file (+ heartbeat markers) on graceful exit, but only while
    /// it still names this incarnation — a successor that already overwrote the file is left
    /// alone. Keeps a later teardown/cleanup from ever acting on this watcher's recycled pid.
    /// </summary>
    public void RemoveOwnPidFile(string key, int ownPid) {
        try {
            var lines = File.ReadAllLines(paths.PidFile(key));

            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var filePid) || filePid != ownPid) return;

            File.Delete(paths.PidFile(key));
            DeleteHeartbeatFiles(key);
        } catch {
            /* best-effort — a missing/unreadable file means there is nothing to retire */
        }
    }

    const int Sigterm = 15;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int sys_kill(int pid, int sig);

    /// <summary>SIGTERM on Unix; false on Windows or when the signal can't be delivered.</summary>
    static bool TrySignalTerm(int pid) {
        if (OperatingSystem.IsWindows()) return false;

        try {
            return sys_kill(pid, Sigterm) == 0;
        } catch {
            return false;
        }
    }

    internal bool PidAlive(string key) => _liveness.PidAlive(key);

    internal bool IsWatcherAlive(string key) => _liveness.IsWatcherAlive(key);

    public async Task EnsureWatcherRunning(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride = null,
            string? cwd               = null,
            bool    skipTitle         = false,
            string  vendor            = "claude",
            int?    parentPid         = null,
            TextWriter? stderr        = null,
            WatcherRun? runsAs        = null
        ) {
        if (agentId is null) ClaimAgentProcess(key, vendor, transcriptPath, cwd);

        if (IsWatcherAlive(key)) {
            return; // fast path: no lock needed to observe an already-healthy watcher.
        }

        // Outside the spawn lock: the daemon takes that lock to claim the source, and would lose it to this hook. A stale
        // claim goes too: nothing may be coming back for it, and a daemon that is up can take it over.
        var handOver = _claims.Read(key) is RecorderClaimState.None or RecorderClaimState.DaemonStale
            ? await HandToDaemonAsync(new RecordingSource(sessionIdOverride ?? key, agentId, transcriptPath, vendor, cwd), skipTitle)
            : RecordingHandOver.Unanswered;

        if (handOver is RecordingHandOver.Taken) return;

        // Both the reap of a wedged watcher and the spawn run under the per-key spawn lock: a
        // hook arriving between the reap's pid-file delete and the new pid file would otherwise
        // see "no pid" and spawn a second watcher.
        await _liveness.WithSpawnLock(key, async () => {
            if (IsWatcherAlive(key)) {
                return;
            }

            // A hook can be past its own check when the person disables the session, and this is what it would start.
            if (DisabledSessions.IsDisabledByPerson(SessionIds.Canonical(sessionIdOverride ?? key)!, config)) {
                return;
            }

            // A hook that can't post can't start a watcher, so taking the claim would leave the source with no recorder.
            if (_claims.Read(key) is not RecorderClaimState.None && (!HookHttp.IsPostable(Url) || !TakeStaleClaim(key, handOver is RecordingHandOver.Declined))) {
                return;
            }

            if (PidAlive(key)) {
                // Alive but its heartbeat is stale: wedged. A watcher the reap could not stop
                // keeps its pid file; spawning over it would leave two watchers on this key, the
                // first one unaddressable. A later call retries the reap.
                var log = stderr ?? Console.Error;
                await log.WriteLineAsync($"Watcher {key} heartbeat stale; reaping wedged watcher and respawning");

                if (!await TryStopWatcher(key, log)) {
                    await log.WriteLineAsync($"Watcher {key} could not be stopped; not respawning");

                    return;
                }
            }

            await spawner.SpawnAsync(
                new WatcherSpawnRequest(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor, parentPid) {
                    Stderr = stderr,
                    RunsAs = runsAs,
                });
        });
    }

    /// <summary>How the daemon answered. However it fails to take the source, the hook arranges a recorder itself: a
    /// watcher costs a hosted session its stamps, and nothing at all would cost it its transcript.</summary>
    async Task<RecordingHandOver> HandToDaemonAsync(RecordingSource source, bool skipTitle) {
        using var budget = new CancellationTokenSource(HandOffBudget, time);

        try {
            return await _daemonRecording.TryRecordAsync(source, skipTitle, budget.Token).WaitAsync(budget.Token);
        } catch {
            return RecordingHandOver.Unanswered;
        }
    }

    /// <summary>Lets go of a daemon's stale claim, for the hook about to record it, under the spawn lock. No claim is left
    /// behind: that is what stops the daemon's recorder if it wakes, and this hook then goes on as it does for a source
    /// nobody claims.</summary>
    /// <param name="declined">The daemon that would catch up is up and said no, so nothing is coming for this claim.</param>
    bool TakeStaleClaim(string key, bool declined = false) {
        if (_claims.Read(key) is not RecorderClaimState.DaemonStale || LeftToCatchUp(key) && !declined) return false;

        _claims.Release(key);

        return _claims.Read(key) is RecorderClaimState.None;
    }

    /// <summary>Whether this hook leaves a stale claim for the daemon's catch-up: only a hosted session's, and only from
    /// its hosted agent, because a watcher would leave that session unstamped for good. From anywhere else it was resumed
    /// by hand while its daemon is down, and no catch-up knows of it.</summary>
    bool LeftToCatchUp(string key) => _hosted.IsHosted && _claims.IsHosted(key);

    /// <summary>Records that the agent process this hook runs under is running <paramref name="key"/>'s
    /// session, when the vendor's process hosts one session and the hook can see it.</summary>
    public void ClaimAgentProcess(string key, string vendor, string? transcriptPath = null, string? cwd = null) {
        if (AgentSessions.HostsOneSession(vendor) && SessionId.Parse(key) is { } session
         && ProcessHelpers.GetCodingAgentPid(vendor, allowFallback: false) is { } agentPid)
            AgentSessions.OnThisMachine(config).Claim(agentPid, session, OriginOf(vendor, transcriptPath, cwd, HostedAgent.FromEnvironment()));
    }

    ClaimOrigin OriginOf(string vendor, string? transcriptPath, string? cwd, HostedAgent hosted) =>
        new(vendor, Url, transcriptPath, cwd, hosted.AgentId, hosted.DaemonEpoch);

    public void SpawnWhatsDoneGenerator(string sessionId, string vendor = "claude", TextWriter? stderr = null) {
        try {
            var kcapPath = Environment.ProcessPath ?? "kcap";

            var psi = new ProcessStartInfo(kcapPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            // The child process picks the headless CLI runner from this flag —
            // matches the `generate-whats-done [--codex] <id>` surface in Program.cs.
            if (vendor == "codex") {
                psi.ArgumentList.Add("--codex");
            }

            // Don't let this detached child inherit the agent's pipe descriptors —
            // same pipe-leak hazard as the watcher spawn above.
            if (starter.StartDetached(psi) is not { } pid) {
                (stderr ?? Console.Error).WriteLine($"Failed to spawn what's-done generator for {sessionId}");

                return;
            }

            (stderr ?? Console.Error).WriteLine($"Spawned what's-done generator for {sessionId} (PID {pid})");
        } catch (Exception ex) {
            (stderr ?? Console.Error).WriteLine($"Failed to spawn what's-done generator for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>Spawns a detached <c>kcap generate-title</c> for a session whose watcher is stopping before its
    /// title could be generated. Only the transcript's path goes on the command line, never the prompt.</summary>
    public void SpawnTitleGenerator(string sessionId, string vendor, string transcriptPath, TextWriter? stderr = null) {
        if (!HookHttp.IsPostable(Url)) {
            (stderr ?? Console.Error).WriteLine(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"title generator not started for {sessionId}"));
            return;
        }

        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };
            psi.ArgumentList.Add(GenerateTitleCommand.Name);
            psi.ArgumentList.Add(sessionId);
            psi.ArgumentList.Add(vendor);
            psi.ArgumentList.Add(transcriptPath);

            (stderr ?? Console.Error).WriteLine(starter.StartDetached(psi) is { } pid
                ? $"Spawned title generator for {sessionId} (PID {pid})"
                : $"Failed to spawn title generator for {sessionId}");
        } catch (Exception ex) {
            (stderr ?? Console.Error).WriteLine($"Failed to spawn title generator for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns a detached, short-lived <c>kcap copilot-finalize</c> process that delivers the transcript
    /// tail Copilot writes after its sessionEnd hook, then <paramref name="endBody"/>. Being detached it
    /// survives Copilot killing the hook. Fire-and-forget and idempotent; mirrors
    /// <see cref="SpawnWhatsDoneGenerator"/>.
    /// </summary>
    public void SpawnCopilotFinalizeDrain(string sessionId, string transcriptPath, string endBody) {
        // A detached child that would poll for up to 45s and then exit 2 on an unusable URL, leaving
        // no marker any assertion could observe. Refuse to launch it at all.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"copilot finalize drain not started for {sessionId}"));
            return;
        }

        try {
            var kcapPath = Environment.ProcessPath ?? "kcap";

            var psi = new ProcessStartInfo(kcapPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };
            psi.ArgumentList.Add("copilot-finalize");
            psi.ArgumentList.Add(sessionId);
            psi.ArgumentList.Add(transcriptPath);
            psi.ArgumentList.Add(endBody);

            // Don't let this detached child inherit the agent's pipe descriptors —
            // same pipe-leak hazard as the spawns above. The child writes its own output to a
            // log file, so it needs no streams from here.
            if (starter.StartDetached(psi) is not { } pid) {
                Console.Error.WriteLine($"Failed to spawn copilot finalize drain for {sessionId}");

                return;
            }

            Console.Error.WriteLine($"Spawned copilot finalize drain for {sessionId} (PID {pid})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn copilot finalize drain for {sessionId}: {ex.Message}");
        }
    }

    public async Task InlineDrainAsync(
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string  vendor = "claude"
        ) {
        // The disable let the daemon's claim go, so past this a source reads as nobody's and this would send all of it.
        if (DisabledSessions.IsDisabledByPerson(SessionIds.Canonical(sessionId)!, config)) return;

        var key   = new RecordingSource(sessionId, agentId, transcriptPath, vendor, null).Key;
        var claim = _claims.Read(key);

        // A hook's drain carries no stamps, and one that wins makes the server discard the daemon's stamped copy.
        if (claim is RecorderClaimState.DaemonFresh) {
            await AskDaemonToFinishAsync(key, sessionId);
            return;
        }

        if (claim is RecorderClaimState.DaemonStale && LeftToCatchUp(key)) {
            await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: left to the daemon's catch-up, which holds {key}");
            return;
        }

        // Runs on session-end BEFORE the lifecycle POST. The caller's preceding KillWatcher is
        // unaffected.
        if (!HookHttp.IsPostable(Url)) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"inline drain skipped for {sessionId}"));
            return;
        }

        try {
            if (claim is RecorderClaimState.DaemonStale && !await TryTakeStaleClaimAsync(key)) {
                // A beat that landed first means the daemon woke, and the source is its to finish.
                if (_claims.Read(key) is RecorderClaimState.DaemonFresh) {
                    await AskDaemonToFinishAsync(key, sessionId);
                } else {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: {key} changed hands; leaving it to its holder");
                }

                return;
            }

            using var httpClient = await http.ForBackgroundAsync();

            // Get server's last recorded position
            int startLine;

            try {
                var query = agentId is not null ? $"?agentId={agentId}" : "";
                var resp  = await httpClient.GetWithRetryAsync($"{Url}/api/sessions/{sessionId}/last-line{query}", time);

                if (resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent) {
                    var json = await resp.Content.ReadAsStringAsync();
                    var doc  = JsonDocument.Parse(json);

                    startLine = (int?)doc.RootElement.Num("last_line_number") + 1
                     ?? WatchCommand.CountFileLines(transcriptPath);
                } else {
                    startLine = WatchCommand.CountFileLines(transcriptPath);
                }
            } catch {
                startLine = WatchCommand.CountFileLines(transcriptPath);
            }

            if (!File.Exists(transcriptPath)) {
                return;
            }

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();
            var commits        = DrainCommits ?? await GitHook.ObservationAsync(config, sessionId, agentId, cwd: null, time);
            var sentAny        = false;

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            var lineIndex = 0;
            var redaction = DrainRedaction?.Invoke() ?? new InlineDrainRedaction(time);

            while (await reader.ReadLineAsync() is { } line) {
                if (lineIndex < startLine) {
                    lineIndex++;

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line)) {
                    var captured = redaction.Encode(line, ReportLoss);
                    if (captured.Consumed == 0 && redaction.CanRetry) {
                        // A caller's cap can abandon the second try, and nothing posts after that.
                        if (!await SendAsync()) return;
                        captured = redaction.Retry(line, ReportLoss);
                    }
                    if (captured.Consumed == 0) {
                        await Console.Error.WriteLineAsync(
                            $"Inline drain for {sessionId}: line {lineIndex} could not be redacted in time; to send it and the rest, run: kcap import --session {sessionId}");
                        break;
                    }
                    newLines.Add(captured.Lines[0]);
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            if (await SendAsync() && !sentAny) {
                await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: nothing new to send");
            }

            void ReportLoss(RedactionLossReason loss, int _) =>
                Console.Error.WriteLine($"Inline capture loss at line {lineIndex}: {CaptureLossMarker.ReasonName(loss)}");

            // False when the drain has to stop, having printed why.
            async Task<bool> SendAsync() {
                commits = commits.Collect();

                if (newLines.Count == 0 && commits.Pending is not { Length: > 0 }) return true;

                var batch = new TranscriptBatch {
                    SessionId       = sessionId,
                    AgentId         = agentId,
                    Lines           = [..newLines],
                    LineNumbers     = [..newLineNumbers],
                    Vendor          = vendor == "claude" ? null : vendor,
                    ObservedCommits = commits.Pending,
                };

                try {
                    foreach (var chunk in TranscriptBatchBuffer.Split(batch)) {
                        var batchJson = JsonSerializer.Serialize(chunk, CapacitorJsonContext.Default.TranscriptBatch);
                        using var content = new StringContent(batchJson, Encoding.UTF8, "application/json");
                        using var resp = await httpClient.PostWithRetryAsync($"{Url}/hooks/transcript", content, time);
                        if (!resp.IsSuccessStatusCode) {
                            await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server returned HTTP {(int)resp.StatusCode}");
                            PrintRecoveryHint(sessionId);
                            return false;
                        }
                        await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: sent {chunk.Lines.Length} line(s)");
                    }
                } catch (HttpRequestException ex) {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server unreachable after retries — {ex.Message}");
                    PrintRecoveryHint(sessionId);
                    return false;
                }

                commits = commits.Delivered(batch.ObservedCommits);
                newLines.Clear();
                newLineNumbers.Clear();
                sentAny = true;

                return true;
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Inline drain for {sessionId} failed: {ex.Message}");
            PrintRecoveryHint(sessionId);
        }
    }

    /// <summary>Waits for the daemon inside <see cref="FinishSourceBudget"/>. Whatever comes back, the hook drains
    /// nothing: the source is the daemon's, which finishes it whether or not anyone is still waiting.</summary>
    async Task AskDaemonToFinishAsync(string key, string sessionId) {
        using var budget = new CancellationTokenSource(FinishSourceBudget, time);

        try {
            var finished = await _daemonRecording.FinishSourceAsync(key, budget.Token).WaitAsync(budget.Token);

            await Console.Error.WriteLineAsync(finished
                ? $"Inline drain for {sessionId}: the daemon finished {key}"
                : $"Inline drain for {sessionId}: left to the daemon, which holds {key}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: left to the daemon, which holds {key}: {ex.Message}");
        }
    }

    async Task<bool> TryTakeStaleClaimAsync(string key) {
        var taken = false;

        await _liveness.WithSpawnLock(key, () => {
            taken = TakeStaleClaim(key);

            return Task.CompletedTask;
        });

        return taken;
    }

    static void PrintRecoveryHint(string sessionId) =>
        Console.Error.WriteLine($"Transcript not uploaded. To import later, run: kcap import --session {sessionId}");
}
