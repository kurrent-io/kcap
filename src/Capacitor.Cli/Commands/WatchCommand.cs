using Capacitor.Cli.Recording;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Titles;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class WatchCommand(
        ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses,
        ICapacitorHttpClient http, ICredentialSource credentials, WatcherManager watchers,
        GitProviderRouter router, TimeProvider time, AccountStore accounts, UserHome home, CapacitorServer server) : IRecorderVendor {
    readonly WatcherPaths _paths = WatcherPaths.FromEnvironment(config);

    // RunWatch sets these once: a WatchCommand hosts one recorder, and what its housekeeping keeps
    // between ticks lives here until that recorder is done.
    RecordingSource _source      = null!;
    Action          _beat        = null!;
    Action          _stop        = null!;
    Action          _onConnected = null!;
    bool            _skipTitle;
    int?            _parentPid;

    IHarnessTitleStore? _titleStore;

    readonly List<MistralVibeSubagent> _vibeSubagents = [];
    readonly GrokSubagentTracker       _grokSubagents = new();
    readonly MuseChildWatchers         _museChildren  = new();

    // Gemini fires no subagent hooks, so the parent discovers nested subagent transcripts itself and
    // spawns a child watcher per subagent. Tracks the files already registered and spawned, so each is
    // handled exactly once across ticks.
    readonly HashSet<string> _seenSubagents = new(StringComparer.Ordinal);

    // Rollouts in the shared Codex sessions tree whose header proved they are not this session's
    // subagents. Keeps the per-tick scan to one header read per new file; a mid-write header is never
    // cached, so it is retried.
    readonly HashSet<string> _codexRuledOutRollouts = new(StringComparer.Ordinal);

    // The last mtime ChildRolloutAdvanced fired for, per child rollout: backs the re-ensure that
    // respawns a reaped child watcher when its subagent re-engages.
    readonly Dictionary<string, DateTime> _codexRolloutMtimes = new(StringComparer.Ordinal);

    // The parent is the only process that knows these exist, so its teardown must stop them.
    readonly HashSet<string> _spawnedChildWatcherKeys = new(StringComparer.Ordinal);

    // The end the Muse SessionEnd hook handed over, posted after the final drain in place of the
    // parent-exit synthesis.
    MuseSessionEndRequest? _museEndRequest;

    // Idle grace between a Codex collab child's task_complete and its live subagent-stop post. The
    // agent type is read lazily from the child rollout's own header, once.
    TimeSpan       _codexSubagentStopGrace;
    string?        _codexChildAgentType;
    DateTimeOffset _codexStopNextAttemptAt = DateTimeOffset.MinValue;

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>
    /// The watcher's hub connection, carrying the bearer the credential source holds at connect time.
    /// </summary>
    internal HubConnection BuildHubConnection(string hubUrl) {
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options => {
                    // Resolved per connect and per reconnect, not captured: a reconnect after a long
                    // outage must send whatever the credential is by then, not the one this watcher
                    // started with.
                    options.AccessTokenProvider = async () => (await credentials.ResolveAsync(default)).Bearer;
                    options.HttpMessageHandlerFactory = inner =>
                        new BearerRejectionReportHandler(time, report => Log(time, report)) {
                            InnerHandler = LoopbackConnect.ForHub(new Uri(hubUrl), inner),
                        };
                    HttpClientExtensions.AddObservationHeaders(options.Headers, profiles.Effective?.UpdateCheck == false);
                    options.WebSocketFactory = (context, ct) => BearerRejectionReportHandler.ConnectWebSocketAsync(
                        context.Uri, context.Options.AccessTokenProvider, time, report => Log(time, report), ct);
                }
            )
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, CapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Halve KeepAliveInterval (15s → 7s); see ServerConnection for rationale.
        // ServerTimeout stays at the 30s default for rollout safety.
        hubConnection.KeepAliveInterval = TimeSpan.FromSeconds(7);

        return hubConnection;
    }

    /// <summary>Outcome of deciding whether the parent-exit watchdog can run.</summary>
    internal enum ParentWatchdog {
        /// <summary>Parent PID is alive — start the 5s liveness poll.</summary>
        Monitor,

        /// <summary>No parent PID was supplied — nothing to monitor.</summary>
        NoParentPid,

        /// <summary>A parent PID was supplied but it's already dead at startup
        /// (typically a transient process from bad PID resolution). Must be surfaced,
        /// never silently skipped.</summary>
        ParentAlreadyDead
    }

    /// <summary>What a stopping watcher does with the session end it would post.</summary>
    internal enum SessionEndAction {
        Post,

        /// <summary>The session's hooks claim a new process; the end belongs to that one.</summary>
        RunsOnElsewhere,

        /// <summary>The daemon is stopping the hosted agent for resume, which continues the session.</summary>
        HeldForResume
    }

    internal static SessionEndAction DecideSessionEnd(bool runsOnElsewhere, HostedAgent? hosted, Func<HostedAgent, bool> resuming) =>
        runsOnElsewhere                                     ? SessionEndAction.RunsOnElsewhere
      : hosted is { AgentId: not null } && resuming(hosted) ? SessionEndAction.HeldForResume
      :                                                       SessionEndAction.Post;

    /// <summary>What a resume holds a handed end for, when it does: the agent its hook named, or the session.</summary>
    internal static string? AgentHoldingEndForResume(MuseSessionEndRequest handed, string sessionId) =>
        HandedEndHeld(handed.Payload, sessionId, h => SessionEndFence.ShouldHold(h, sessionId), SessionEndFence.ShouldHold);

    /// <summary>A handed end is judged as the run its hook named, never as the watcher's: a successor watcher can
    /// carry another run's identity. An end that names no run is placed by its session, as an end with no epoch.</summary>
    internal static string? HandedEndHeld(
            string payload, string sessionId, Func<HostedAgent, bool> holdsRun, Func<ExitedSession, bool> holdsSession) {
        if (HandedEndRun(payload) is { } run)
            return DecideSessionEnd(runsOnElsewhere: false, run, holdsRun) == SessionEndAction.HeldForResume ? run.AgentId : null;

        return SessionId.Parse(sessionId) is { } session && holdsSession(new ExitedSession(session, null)) ? sessionId : null;
    }

    /// <summary>The run a handed end's hook named, or null when it named none.</summary>
    internal static HostedAgent? HandedEndRun(string payload) {
        try {
            if (JsonNode.Parse(payload) is JsonObject end && end["agent_host_id"]?.GetValue<string>() is { Length: > 0 } agentId)
                return new HostedAgent(agentId, false, DaemonBridge.None) {
                    DaemonEpoch = end["daemon_epoch"]?.GetValue<string>() is { Length: > 0 } epoch ? epoch : null
                };
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        return null;
    }

    /// <summary><paramref name="resumingId"/>, after releasing the handed end's claim when a resume holds
    /// it: a claim left in place would block an end handed over later.</summary>
    internal static string? HoldHandedEndForResume(MuseSessionEndRequest handed, string? resumingId) {
        if (resumingId is not null) MuseSessionEndRequest.Release(handed.ClaimedPath);

        return resumingId;
    }

    /// <summary>Whether a watcher also watches for Kiro Crew finishing with its session: a Kiro session
    /// watcher on a machine where Crew has run.</summary>
    internal static bool WatchesCrewEnd(string vendor, string? agentId, bool crewPresent) =>
        vendor == "kiro" && agentId is null && crewPresent;

    /// <summary>The session-end reason the watcher posts for how it came to stop, or null when it posts none.</summary>
    internal static string? DecideEndReason(bool parentExited, bool crewFinished, bool wedgedCeiling, bool idle, bool copilotShutdown) =>
        parentExited    ? "parent_exited"
      : crewFinished    ? "crew_finished"
      : wedgedCeiling   ? "parent_dead_ceiling"
      : idle            ? "idle_timeout"
      : copilotShutdown ? "session_shutdown"
      :                   null;

    /// <summary>
    /// Decides whether the parent-exit watchdog should run. Pure so the three
    /// outcomes — including the dead-at-startup case that caused stuck sessions —
    /// are unit-testable without spawning processes.
    /// </summary>
    internal static ParentWatchdog DecideParentWatchdog(int? parentPid, Func<int, bool> isAlive) =>
        parentPid is not { } ppid ? ParentWatchdog.NoParentPid
        : !isAlive(ppid)          ? ParentWatchdog.ParentAlreadyDead
        :                           ParentWatchdog.Monitor;

    /// <summary>
    /// Staged outcome for the wedged/parent-dead recovery loop. A watcher that hit
    /// <see cref="ParentWatchdog.ParentAlreadyDead"/> is alive-and-connected with no end path
    /// (the server sweep only sees GONE watchers, and Kiro/OpenCode have no idle fallback), so
    /// recovery re-resolves + re-arms the watchdog when possible and only ends on a long ceiling.
    /// </summary>
    internal enum ParentDeadRecovery {
        /// <summary>Re-resolution found a live parent — re-arm the watchdog; end nothing (preferred).</summary>
        ReArm,

        /// <summary>No live parent yet AND still under the ceiling with recent progress — keep polling.</summary>
        KeepWaiting,

        /// <summary>Resolution keeps failing AND no transcript progress for longer than the ceiling — end.</summary>
        EndTerminal
    }

    /// <summary>
    /// Pure staged decision for the parent-dead recovery loop. Re-arm takes priority (a found,
    /// live parent is the preferred outcome and never ends the session). Otherwise the session ends
    /// ONLY when the no-progress window exceeds the ceiling — a user parked at a Kiro/OpenCode prompt
    /// produces no transcript lines but must not be ended before then, and any new progress resets
    /// <paramref name="noProgressElapsed"/> via the caller.
    /// </summary>
    internal static ParentDeadRecovery DecideParentDeadRecovery(
            int?            reResolvedPid,
            Func<int, bool> isAlive,
            TimeSpan        noProgressElapsed,
            TimeSpan        ceiling
        ) =>
        reResolvedPid is { } pid && pid > 1 && isAlive(pid) ? ParentDeadRecovery.ReArm
        : noProgressElapsed > ceiling                        ? ParentDeadRecovery.EndTerminal
        :                                                      ParentDeadRecovery.KeepWaiting;

    /// <summary>
    /// Long ceiling for the staged parent-dead / wedged-watcher recovery. Deliberately far above the
    /// 60-min idle default: an idle GUI conversation ends via the idle timeout, whereas this ceiling
    /// exists only to eventually end a wedged, alive-but-connected watcher (invisible to the server
    /// sweep) whose parent can't be re-resolved — without false-ending a user parked at a prompt.
    /// </summary>
    internal static readonly TimeSpan DefaultParentDeadCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the parent-dead recovery ceiling from KCAP_PARENT_DEAD_CEILING_MINUTES, falling back
    /// to <see cref="DefaultParentDeadCeiling"/> for unset/blank/non-numeric/non-positive values.
    /// Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveParentDeadCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultParentDeadCeiling;

    public async Task<int> RunWatch(
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string? cwd,
            bool    skipTitle = false,
            int?    parentPid = null,
            string  vendor    = "claude"
        ) {
        // BEFORE the log redirection below: SignalR's WithUrl builds a Uri synchronously, so a
        // relative value fails there — ahead of the connect/retry loop — and `watch` is not
        // fail-open, so the top-level catch turns that into an opaque exit 1. Validating here gives
        // a hand-run watcher the actionable hint on the real stderr and the same exit 2 every
        // interactive command gives; a few lines later Console.Error is a log file nobody is looking at.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(HttpClientExtensions.SchemeMissingHint);
            return 2;
        }

        // Redirect all output to a log file so we don't hold parent's pipe FDs open
        var logDir = config.Path("logs");
        Directory.CreateDirectory(logDir);
        var logKey    = agentId is not null ? $"{sessionId}-{agentId}" : sessionId;
        var logPath   = Path.Combine(logDir, $"{logKey}.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        // logKey is the same key WatcherManager uses for the pid file, so it doubles as the heartbeat
        // key. A hook-side staleness probe reads it to tell a wedged watcher from a healthy one, which
        // a pid-only liveness check can't.
        var heartbeatPath = watchers.GetHeartbeatFilePath(logKey);

        void TouchHeartbeat() {
            try {
                WatcherHeartbeat.Touch(heartbeatPath, time.GetUtcNow());
            } catch {
                /* best-effort — a missed touch just risks one false-stale reading, never worse */
            }
        }

        TouchHeartbeat();

        // Detach from controlling terminal so closing the parent's terminal does
        // not deliver SIGHUP to the watcher. Without this, both the coding agent
        // and the watcher die simultaneously when the user closes the terminal
        // window, and the parent-PID poll below never fires — leaving the
        // session stuck "active" because session-end is never POSTed.
        if (!ProcessHelpers.DetachFromControllingTerminal()) {
            Log(time, "setsid() failed; SIGHUP from terminal close may kill the watcher");
        }

        using var cts = new CancellationTokenSource();

        if (vendor == "claude" && agentId is null)
            _ = Task.Run(() => ClaudeJudgeWarmup.RunAsync(config, sessionId, transcriptPath, cwd, time, cts.Token));

        // Tracks whether shutdown was triggered by the parent coding-agent process
        // dying without firing session-end. When true (1), the watcher takes over the
        // server's session-end POST (which the parent normally fires) so the read
        // model doesn't keep the session "active" forever.
        //
        // Written by the parent-PID monitor task and signal handlers, read on the
        // main thread — use Interlocked/Volatile so the C# memory model formally
        // guarantees the write is observed even though awaits already act as memory
        // barriers in practice.
        var parentExited = 0;
        // set by the staged parent-dead recovery loop when it ends a wedged watcher on the ceiling.
        var wedgedCeilingExit = 0;
        // set when Kiro Crew is finished with a session its still-running kiro-cli process hosts.
        var crewFinished = 0;

        _source    = new RecordingSource(sessionId, agentId, transcriptPath, vendor, cwd);
        _skipTitle = skipTitle;
        _parentPid = parentPid;
        _beat      = TouchHeartbeat;
        _stop      = cts.Cancel;

        var hubConnection = BuildHubConnection($"{Url}/hubs/sessions");
        var transport     = new HubRecorderTransport(hubConnection, _source.Key, TouchHeartbeat, message => Log(time, message), time);

        using var recorder = new TranscriptRecorder(_source, transport, new NoStamps(), time, new RecorderContext(
            server, this, time, message => Log(time, message), TouchHeartbeat));

        var state = recorder.State;

        // The recorder can stop itself, and only cts stops what runs beside it. Left to the end of RunAsync, a
        // parent monitor could see an agent exit right after its end hook and post a parent_exited end.
        using var stopWithRecorder = recorder.Stopping.Register(cts.Cancel);

        // Handle SIGTERM/SIGINT for graceful shutdown.
        //
        // PosixSignalRegistration.Create returns an IDisposable that owns the
        // underlying handler slot; if it's not rooted for the lifetime of the
        // method the finalizer silently unregisters the handler. `using var`
        // keeps both registrations alive until RunWatch returns and disposes
        // them deterministically.
        //
        // For SIGTERM and SIGHUP, ctx.Cancel = true is required: .NET runs the
        // signal's default action (terminate) after the handler unless the
        // context is cancelled, which would kill the watcher before the main
        // loop notices cts and the drain / session-end POST never run.
        //
        // A stop request's sender waits only seconds (a kill 5s later, or the server's drain wait); nothing
        // waits on any other exit, so these tell the recorder rather than only cancelling.
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            recorder.RequestStop();
        };
        using var sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
            recorder.RequestStop();
            ctx.Cancel = true;
        });
        using var stopRequest = WatcherStopSignal.Listen(Environment.ProcessId, () => {
            Log(time, "Stop requested; shutting down watcher");
            recorder.RequestStop();
        });

        // SIGHUP defense-in-depth: setsid above should prevent the kernel from
        // delivering SIGHUP to us, but if a shell forwards SIGHUP explicitly to
        // its process group children before our setsid lands, or if setsid
        // failed, this handler keeps the watcher alive long enough to run the
        // parent-exit cleanup path (drain + session-end POST).
        using var sighupReg = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => {
            Log(time, "Received SIGHUP; treating as parent-exit");
            Interlocked.Exchange(ref parentExited, 1);
            cts.Cancel();
            ctx.Cancel = true;
        });

        // Watch the spawning coding-agent process. If it dies without firing
        // session-end (crash, force-kill, IDE-detach), self-terminate within ~5s and
        // POST session-end instead of orphaning. Crucially, the cases where we DON'T
        // monitor are logged: a silently-skipped watchdog is exactly the failure
        // that left sessions stuck "active" with the watcher still connected — the
        // resolved parent PID was already dead at startup and nothing recorded it.
        // Local so both the initial Monitor case and the recovery re-arm start the identical poll.
        void ArmParentMonitor(int ppid) {
            Log(time, $"Monitoring parent pid {ppid}");
            _ = Task.Run(async () => {
                while (!cts.Token.IsCancellationRequested) {
                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), time, cts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }

                    if (!ProcessHelpers.IsProcessAlive(ppid)) {
                        Log(time, $"Parent pid {ppid} exited; shutting down watcher");
                        Interlocked.Exchange(ref parentExited, 1);
                        cts.Cancel();

                        return;
                    }
                }
            }, cts.Token);
        }

        var armDeadParentOnConnect = false;

        switch (DecideParentWatchdog(parentPid, ProcessHelpers.IsProcessAlive)) {
            case ParentWatchdog.NoParentPid:
                Log(time, "No parent pid supplied; parent-exit watchdog disabled (session-end relies on the agent's own hook)");

                break;

            case ParentWatchdog.ParentAlreadyDead when EndsWhenParentFoundDead(vendor, agentId):
                armDeadParentOnConnect = true;

                break;

            case ParentWatchdog.ParentAlreadyDead:
                Log(time, $"Parent pid {parentPid} already dead at watcher startup; entering staged recovery — "
                  + "will periodically re-resolve + re-arm the watchdog, and only end after a long ceiling "
                  + "with no transcript progress and continued resolution failure.");

                // Staged recovery: re-resolve the durable agent (using the vendor alias) and re-arm if
                // found; otherwise end ONLY after `ceiling` of no transcript progress. Any new progress
                // resets the window (state.LastActivityAt advances on drained lines). Session watchers
                // only — subagent watchers are torn down by the parent's lifecycle.
                if (agentId is null) {
                    var ceiling = ResolveParentDeadCeiling(Environment.GetEnvironmentVariable("KCAP_PARENT_DEAD_CEILING_MINUTES"));

                    _ = Task.Run(async () => {
                        while (!cts.Token.IsCancellationRequested) {
                            try {
                                await Task.Delay(TimeSpan.FromSeconds(30), time, cts.Token);
                            } catch (OperationCanceledException) {
                                return;
                            }

                            // No fallback: this watcher has been reparented, so the heuristic here
                            // resolves systemd/init rather than the agent.
                            var reResolved        = ProcessHelpers.GetCodingAgentPid(vendor, allowFallback: false);
                            var noProgressElapsed = time.GetUtcNow() - state.LastActivityAt;

                            switch (DecideParentDeadRecovery(reResolved, ProcessHelpers.IsProcessAlive, noProgressElapsed, ceiling)) {
                                case ParentDeadRecovery.ReArm:
                                    Log(time, $"Parent re-resolved to live pid {reResolved}; re-arming the watchdog");
                                    ArmParentMonitor(reResolved!.Value);

                                    return;

                                case ParentDeadRecovery.EndTerminal:
                                    Log(time, $"Parent unresolved and no transcript progress for >{ceiling.TotalMinutes:F0}m; "
                                      + "ending session (parent_dead_ceiling)");
                                    Interlocked.Exchange(ref wedgedCeilingExit, 1);
                                    cts.Cancel();

                                    return;

                                case ParentDeadRecovery.KeepWaiting:
                                default:
                                    break; // poll again
                            }
                        }
                    }, cts.Token);
                }

                break;

            case ParentWatchdog.Monitor:
                ArmParentMonitor(parentPid!.Value);

                break;
        }

        // A recorder connects inside RunAsync, and these are armed only once it has: a cancel before then
        // skips the final drain and end post.
        _onConnected = () => {
            if (armDeadParentOnConnect) ArmParentMonitor(parentPid!.Value);

            // Crew keeps one kiro-cli process for a whole chat and its in-process sub-agents, so the
            // parent-pid watchdog alone would leave a finished sub-agent or a replaced chat session open.
            if (WatchesCrewEnd(vendor, agentId, harnesses.Of<KiroHarness>().Crew.IsPresent())) {
                var crewEnd = new KiroCrewSessionEndWatch(harnesses.Of<KiroHarness>().Crew, harnesses.Of<KiroHarness>().Paths.SessionsDir, sessionId, time);

                // Checks once at once, so the session's chat is learned before Crew can drop it.
                _ = Task.Run(async () => {
                    for (var first = true; !cts.Token.IsCancellationRequested; first = false) {
                        if (!first) {
                            try {
                                await Task.Delay(TimeSpan.FromSeconds(5), time, cts.Token);
                            } catch (OperationCanceledException) {
                                return;
                            }
                        }

                        if (crewEnd.IsFinished()) {
                            Log(time, "Kiro Crew is finished with this session; shutting down watcher");
                            Interlocked.Exchange(ref crewFinished, 1);
                            cts.Cancel();

                            return;
                        }
                    }
                }, cts.Token);
            }
        };

        hubConnection.Reconnecting += ex => {
            Log(time, $"SignalR reconnecting: {ex?.Message}");

            return Task.CompletedTask;
        };

        hubConnection.Closed += ex => {
            Log(time, $"SignalR connection closed: {ex?.Message}");
            cts.Cancel();

            return Task.CompletedTask;
        };

        try {
            await recorder.RunAsync(cts.Token);
        } catch (OperationCanceledException) when (transport.StoppedBeforeConnect) {
            await hubConnection.DisposeAsync();

            // A watcher stopped before it connected is still the one the Muse hook may have handed an
            // end to: it was alive when the hook looked. Nothing has been streamed yet, so the log is
            // drained inline ahead of the end, as the hook would have.
            if (TakesHandedMuseEnd(vendor, agentId)) {
                watchers.RemoveOwnPidFile(sessionId, Environment.ProcessId);

                if (ClaimHandedMuseEnd(sessionId) is { } handed) {
                    await watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: vendor);
                    await SettleHandedEndsAsync(sessionId, transcriptPath, cwd, vendor, repository: null, handed);
                }
            }

            await logWriter.DisposeAsync();

            return 0;
        }

        // The Kiro hook posted session-start before this watcher spawned, so a session Crew finished
        // below the buffering threshold still sends its lines and its end rather than staying open.
        if (Volatile.Read(ref crewFinished) == 1 && !state.ThresholdReached) {
            state.ThresholdReached = true;
            state.BufferedLines.Clear();
            state.BufferedLineNumbers.Clear();
        }

        await recorder.FinishAsync(CancellationToken.None);

        transcriptPath = recorder.TranscriptPath;

        // Stop spawned child watchers on every parent exit path, SIGTERM-first, so each runs its final
        // drain before the session-end post below. A SIGKILLed parent skips this, and the codex-child
        // reap ceiling backstops that orphan.
        if (_spawnedChildWatcherKeys.Count > 0) {
            Log(time, $"Stopping {_spawnedChildWatcherKeys.Count} spawned child watcher(s)");
            await watchers.KillWatchers(_spawnedChildWatcherKeys);
        }

        Log(time, $"Done. {state.LinesProcessed} total lines processed.");

        await hubConnection.DisposeAsync();

        // Parent coding-agent died without firing session-end. Take over the role:
        // POST /hooks/session-end so the server writes SessionEnded (otherwise the
        // session stays "active" forever in the read model). Agent watchers skip it: the
        // parent session's own end covers them. A session with no real turn is posted too —
        // its start hook already registered it, so skipping the end leaves it Live for good.
        // Runs after SignalR dispose so the server usually finds no watcher to drain; one that has
        // not yet processed the disconnect waits out its drain timeout, which the POST budget covers.
        var endReason = DecideEndReason(
            parentExited:  Volatile.Read(ref parentExited)      == 1,
            crewFinished:  Volatile.Read(ref crewFinished)      == 1,
            wedgedCeiling: Volatile.Read(ref wedgedCeilingExit) == 1,
            idle:          recorder.EndedIdle,
            // Read again: a resume can land after the grace ran out, and the final drain does not always run.
            copilotShutdown: recorder.EndedOnCopilotShutdown && !CopilotFinalizeDrainCommand.MovedPastShutdown(transcriptPath));

        // Cursor's idle-ceiling exit must not synthesize a session end: end synthesis has one owner, the
        // sessionEnd hook or the server's lease-gated sweep, and posting here would race whichever fires.
        // The watcher only exits and final-drains. Its other exit paths post as usual.
        var cursorSuppressesEndPost = CursorSuppressesEndPost(vendor, recorder.EndedIdle);

        // Normally a no-op: the final drain already promoted any read fallback in the batch that reached the
        // server. This matters only if that drain didn't run, for instance when the transcript was gone.
        if (state.EvidenceScanner is { } scanner && await scanner.PromoteReadFallbackAsync() is { } fallbackRepo) {
            TranscriptRecorder.ApplyEvidenceRepo(state, fallbackRepo);
        }

        // Graceful exit: retire this incarnation's pid file so no later teardown/cleanup can act
        // on a recycled pid (KillWatcher's token guard is the crash-exit backstop). It goes before the
        // last look for a handed end: the Muse hook checks for a live watcher after writing a request,
        // so an end written after this look finds none and gets a watcher of its own.
        watchers.RemoveOwnPidFile(
            agentId is null ? sessionId : $"{sessionId}-{agentId}", Environment.ProcessId);
        recorder.RetireAckCursor();

        if (TakesHandedMuseEnd(vendor, agentId)) _museEndRequest ??= ClaimHandedMuseEnd(sessionId);

        if (_museEndRequest is { } handedEnd) {
            await SettleHandedEndsAsync(sessionId, transcriptPath, cwd, vendor, state.Repository, handedEnd);
        } else if (PostsOwnSessionEnd(endReason, agentId, cursorSuppressesEndPost)) {
            // A resumed session's hooks claim its new process; the end of the one this watcher followed
            // is then not the session's end, and the new process is left without a watcher.
            var claimant = LiveClaimant(sessionId);
            var hosted   = HostedAgent.FromEnvironment();

            switch (DecideSessionEnd(SessionRunsOnElsewhere(vendor, agentId, claimant), hosted, h => SessionEndFence.ShouldHold(h, sessionId))) {
                case SessionEndAction.RunsOnElsewhere:
                    Log(time, $"Not posting a {endReason} session-end: the session runs on in pid {claimant}");
                    await ArmSuccessorAsync(sessionId, transcriptPath, cwd, vendor, claimant);
                    break;
                case SessionEndAction.HeldForResume:
                    Log(time, $"session end suppressed: agent {hosted?.AgentId} is resuming");
                    break;
                default:
                    await PostSessionEndOnParentExitAsync(sessionId, transcriptPath, cwd, vendor, state.Repository, endReason);
                    break;
            }
        }

        await logWriter.DisposeAsync();

        return 0;
    }

    public async Task PrepareAsync(WatchState state, CancellationToken ct) {
        var (sessionId, agentId, transcriptPath, vendor, cwd) = _source;

        _titleStore = HarnessTitleStores.For(vendor, agentId, sessionId, transcriptPath, harnesses, accounts.TryLoad(), home);
        if (vendor == "codex" && CodexRolloutReadings.For(accounts, transcriptPath, home) is { } codexReadings) state.ObserveCodexReadings = lines => codexReadings.Observe(lines);
        if (_titleStore is not null) state.TitleTracker = new HarnessTitleTracker(_titleStore.RecordsChangeTime);

        state.CopilotHostMissing = vendor == "copilot" && agentId is null && _parentPid is null;
        _codexSubagentStopGrace  = CodexSubagentTurnTracker.ResolveStopGrace(Environment.GetEnvironmentVariable("KCAP_CODEX_SUBAGENT_IDLE_MINUTES"));

        if (_skipTitle) {
            state.TitleGenerated = true;
        }

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time);
            state.LastRepoDetection = time.GetUtcNow();
        }

        state.Commits           = await GitHook.ObservationAsync(config, sessionId, agentId, cwd, time);
        state.LastCoverageCheck = time.GetUtcNow();

        if (vendor == "claude" && agentId is null) {
            state.SecondaryRoots    = new SecondaryRepoRoots(GitRepository.FindRoot, cwd is null ? null : GitRepository.FindRoot(cwd));
            state.SubagentCheckouts = new ClaudeSubagentCheckouts(transcriptPath);
        }

        // No cwd-derived repo (launched outside any checkout, or no cwd at all): fall back to scanning the
        // transcript's own tool-use paths for a git root. Session watchers only, matching the cwd detection
        // above: a subagent watcher is always spawned with no cwd and never had its own repo detection.
        if (vendor == "claude" && agentId is null && (cwd is null || GitRepository.FindRoot(cwd) is null)) {
            state.EvidenceScanner = new RepoEvidenceScanner<RepositoryPayload>(
                GitRepository.FindRoot, root => RepositoryDetection.DetectRepositoryAsync(router, config, root, time),
                p => p.Owner is not null && p.RepoName is not null);

            try {
                foreach (var line in File.ReadLinesShared(transcriptPath)) {
                    if (await state.EvidenceScanner.OnLineAsync(vendor, line) is { } repo) {
                        TranscriptRecorder.ApplyEvidenceRepo(state, repo);

                        break;
                    }
                }
            } catch {
                // fail-open: this one-shot prefix scan is best-effort and must never block startup
            }
        }
    }

    public void OnConnected() => _onConnected();

    // Vibe replaces journal segments as it publishes, so a refresh can race a file away; the next drain
    // retries it.
    public void RefreshTranscript(string transcriptPath) {
        if (_source.Vendor != "mistral-vibe") return;

        try { _vibeSubagents.AddRange(MistralVibeLiveTranscript.Refresh(transcriptPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task BeforeDrainAsync(WatchState state, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, cwd) = _source;

        // Periodically refresh repository info (every 60s)
        if (cwd is not null && time.GetUtcNow() - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
            var detected = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time);

            // An evidence-derived repo may only be replaced by another real detection,
            // never cleared back to null by a launch-cwd probe that still finds nothing.
            if (ShouldReplaceRepository(detected, state.RepositoryFromEvidence)) {
                state.Repository = detected;
            }

            state.LastRepoDetection = time.GetUtcNow();
        }

        if (state.Commits is not CommitObservation.Uncovered && time.GetUtcNow() - state.LastCoverageCheck > TimeSpan.FromSeconds(60)) {
            state.Commits           = state.Commits.Rechecked(await GitHook.CoversAsync(config, sessionId, cwd, time));
            state.LastCoverageCheck = time.GetUtcNow();
        }

        if (time.GetUtcNow() - state.LastSubagentCheckoutScan >= SubagentCheckoutScanGap) {
            ObserveSubagentCheckouts(state, time);
            state.LastSubagentCheckoutScan = time.GetUtcNow();
        }

        if (SecondaryProbeDue(state, time.GetUtcNow())) {
            await LinkSecondaryPullRequestsAsync(state, sessionId, SecondaryProbeBudget, ct, _beat);
            state.LastSecondaryProbe = time.GetUtcNow();
        }

        if (state.PendingCreatedPullRequests.Count > 0 && time.GetUtcNow() >= state.NextCreatedPullRequestLink) {
            await LinkCreatedPullRequestsAsync(state, sessionId, SecondaryProbeBudget, ct, lastChance: false, _beat);
            // Only a PR left pending waits; one created after a clean pass goes at once.
            state.NextCreatedPullRequestLink = state.PendingCreatedPullRequests.Count > 0 ? time.GetUtcNow() + TimeSpan.FromSeconds(60) : default;
        }

        // Before the first drain, so a transcript title line can be judged by the time LLM titling is due.
        if (agentId is null && TranscriptTitleLines.MayNeedHarnessTitles(vendor) && state.ServerRecordsHarnessTitles is null
         && time.GetUtcNow() - state.LastHarnessTitleProbe >= HarnessTitleRetryGap) {
            state.LastHarnessTitleProbe      = time.GetUtcNow();
            state.ServerRecordsHarnessTitles = await ProbeHarnessTitlesAsync(ct, _beat);
        }

        // Read once before the first drain, so a store title the server records latches HarnessTitleSeen
        // before that drain can trigger LLM titling.
        if (_titleStore is not null && state.LastHarnessTitleRead == default) {
            state.LastHarnessTitleRead = time.GetUtcNow();
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat);
        }
    }

    public async Task AfterDrainAsync(WatchState state, string transcriptPath, IReadOnlyList<string> drained, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, _) = _source;

        // Also polled on its own cadence: some stores (Cursor's meta.json, Antigravity's sqlite row) change
        // while the transcript stays idle.
        if (_titleStore is not null && (drained.Count > 0 || time.GetUtcNow() - state.LastHarnessTitleRead >= HarnessTitleRetryGap)) {
            state.LastHarnessTitleRead = time.GetUtcNow();
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat);
        }

        // Live subagent discovery: only the parent watcher scans; child watchers just stream their file.
        // Gemini scans its native nested chat files; OpenCode scans the nested dir the kcap plugin writes
        // child {info,parts} into; Codex scans the shared sessions tree for collab rollouts whose
        // session_meta names this session as parent_thread_id; Antigravity links subagents from the
        // INVOKE_SUBAGENT steps drained this tick, nesting only, since subagents are captured standalone.
        if (agentId is null && vendor == "gemini") {
            await ScanGeminiSubagents(sessionId, transcriptPath, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "opencode") {
            await ScanOpenCodeSubagents(sessionId, transcriptPath, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "codex") {
            await ScanCodexSubagents(sessionId, transcriptPath, _seenSubagents, _codexRuledOutRollouts,
                _codexRolloutMtimes, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "antigravity") {
            await ScanAntigravitySubagentLinks(sessionId, drained, state.PostedSubagentLinks, ct);
        } else if (agentId is null && vendor == "mistral-vibe") {
            await ScanMistralVibeSubagents(sessionId, transcriptPath, _vibeSubagents, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "grok") {
            await ScanGrokSubagents(sessionId, transcriptPath, drained, _grokSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "muse") {
            await ScanMuseSubagents(sessionId, transcriptPath, _seenSubagents, _museChildren, _spawnedChildWatcherKeys, ct);
            await StopFinishedMuseChildren(sessionId, transcriptPath, drained, _museChildren);

            if (ClaimHandedMuseEnd(sessionId) is { } handed) {
                Log(time, "Muse SessionEnd hook handed its session-end to this watcher; draining and posting it");
                _museEndRequest = handed;
                _stop();
            }
        }

        // A Codex collab child posts its own subagent-stop once its rollout's last turn completed and the
        // grace elapsed; otherwise the parent's end is the only stop, and a finished child's card spins for
        // the parent's whole lifetime. The server dedupes lifecycle events, so the tracker latches after the
        // first success, and a failed post retries on its own interval. The child keeps streaming after the
        // stop, since appends are not lifecycle-gated.
        if (agentId is not null && vendor == "codex"
         && time.GetUtcNow() >= _codexStopNextAttemptAt
         && state.CodexSubagentTurn.ShouldPostStop(
                state.PendingCodexToolCalls.Count, state.LastActivityAt,
                time.GetUtcNow(), _codexSubagentStopGrace)) {
            _codexChildAgentType ??= ResolveCodexChildAgentType(transcriptPath);

            if (await PostCodexSubagentStopAsync(sessionId, agentId, _codexChildAgentType, transcriptPath, ct)) {
                state.CodexSubagentTurn.StopPosted = true;
                Log(time, $"Codex subagent {agentId} ({_codexChildAgentType}) turn complete + idle "
                  + $"{_codexSubagentStopGrace.TotalMinutes:F0}m; posted subagent-stop");
            } else {
                _codexStopNextAttemptAt = time.GetUtcNow() + CodexSubagentStopRetryInterval;
            }
        }
    }

    public async Task AfterFinalDrainAsync(WatchState state, IReadOnlyList<string> drained, long shutdownStarted, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, _) = _source;

        // The parent may have emitted an INVOKE_SUBAGENT step after the loop's last tick, and this is the
        // last chance to link it.
        if (agentId is null && vendor == "antigravity") {
            await ScanAntigravitySubagentLinks(sessionId, drained, state.PostedSubagentLinks, ct);
        }

        // A store the transcript does not carry can change after the loop's last read; this is its last chance.
        if (_titleStore is not null) {
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat,
                FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted));
        }

        // A PR is usually opened in the session's last turn, after the previous 60s probe.
        await LinkCreatedPullRequestsAsync(state, sessionId,
            FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted), ct, lastChance: true, _beat);
        ObserveSubagentCheckouts(state, time, final: true);
        await LinkSecondaryPullRequestsAsync(state, sessionId,
            FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted), ct, _beat);
    }

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) =>
        AntigravityGenMetadataDb.ReadUsageLines(dbPath, afterIdx, createdAt);

    public Task<ClaudeCliResult?> GenerateTitleAsync(string userText, string? assistantText, CancellationToken ct) =>
        TitleGeneration.GenerateAsync(userText, assistantText, time, m => Log(time, m), profiles.Resolution.Profile, harnesses, _source.Vendor, ct);

    public void HandOffTitle(string transcriptPath) => watchers.SpawnTitleGenerator(_source.SessionId, _source.Vendor, transcriptPath);

    /// <summary>
    /// Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
    /// transcripts itself (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>). On first
    /// sight of a file it registers the subagent (<c>subagent-start</c>, fail-closed) then
    /// spawns a detached child watcher that streams it with the subagent's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <paramref name="seen"/>;
    /// deterministic server-side lifecycle ids make re-registration safe.
    /// </summary>
    async Task ScanGeminiSubagents(
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = GeminiSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        IReadOnlyDictionary<string, string>? types = null;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var subId = Path.GetFileNameWithoutExtension(subFile);
            if (!Guid.TryParse(subId, out _)) continue; // not a <subId>.jsonl

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(subId);
            types       ??= GeminiSubagentDiscovery.ResolveAgentTypes(transcriptPath);
            var agentType = types.GetValueOrDefault(subId) ?? "subagent";

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostSubagentStartAsync(sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await watchers.EnsureWatcherRunning(key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "gemini");
            spawnedChildKeys.Add($"{sessionId}-{agentId}");

            Log(time, $"Gemini subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    /// <summary>
    /// Vibe fires no hooks for a subagent; the child runs in its own <c>child-&lt;hex&gt;</c> store
    /// beside the parent's, named only by the parent's <c>subagent.spawn</c> effect. The parent
    /// watcher registers each one (<c>subagent-start</c>, fail-closed), keeps a copy of its store, and
    /// spawns a child watcher to stream that copy. A child whose store is not there yet, or whose
    /// registration failed, is retried on the next tick.
    /// </summary>
    async Task ScanMistralVibeSubagents(
            string                    sessionId,
            string                    transcriptPath,
            List<MistralVibeSubagent> found,
            HashSet<string>           seen,
            ICollection<string>       spawnedChildKeys,
            CancellationToken         ct
        ) {
        if (found.Count == 0 || MistralVibeLiveTranscript.SourceOf(transcriptPath) is not { } source) return;
        if (Path.GetDirectoryName(source.SessionDir) is not { } storeRoot) return;

        // Every refresh re-reports each spawn the store holds, so one child arrives many times.
        var pending = found.DistinctBy(s => s.ChildSessionId, StringComparer.Ordinal).ToList();
        found.Clear();

        foreach (var subagent in pending) {
            if (ct.IsCancellationRequested) return;
            if (subagent.AgentId is not { } agentId || seen.Contains(agentId)) continue;

            var childDir = Path.Combine(storeRoot, subagent.ChildSessionId);
            if (!Directory.Exists(childDir)) { found.Add(subagent); continue; }

            var key       = $"{sessionId}-{agentId}";
            var childCopy = MistralVibeLiveTranscript.PathFor(config, key);
            MistralVibeLiveTranscript.Follow(childDir, childCopy, source.Model);

            if (!await PostSubagentStartAsync(sessionId, agentId, subagent.AgentType ?? "subagent", childCopy, ct)) {
                found.Add(subagent);
                continue;
            }

            seen.Add(agentId);
            await watchers.EnsureWatcherRunning(key: key, transcriptPath: childCopy,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "mistral-vibe");
            spawnedChildKeys.Add(key);

            Log(time, $"Mistral Vibe subagent {agentId} registered + child watcher spawned");
        }
    }

    /// <summary>
    /// Grok fires no hook for a subagent: it runs as a sibling session the parent's transcript names when
    /// it is spawned and when it finishes. A spawned child is registered (<c>subagent-start</c>,
    /// fail-closed) and streamed by a child watcher once its transcript exists, retried each tick until
    /// then; a finished one is drained and stopped.
    /// </summary>
    async Task ScanGrokSubagents(
            string                sessionId,
            string                transcriptPath,
            IReadOnlyList<string> drained,
            GrokSubagentTracker   tracker,
            ICollection<string>   spawnedChildKeys,
            CancellationToken     ct
        ) {
        if (tracker.Seeded) tracker.Observe(GrokSubagents.Read(drained));
        else {
            try { tracker.Seed(GrokSubagents.Read(File.ReadLinesShared(transcriptPath))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { tracker.Observe(GrokSubagents.Read(drained)); }
        }

        foreach (var (child, type) in tracker.AwaitingStart()) {
            if (ct.IsCancellationRequested) return;

            var transcript = GrokSubagents.ChildTranscript(transcriptPath, child);
            if (!File.Exists(transcript)) continue;

            var agentId = GrokSubagents.AgentId(child);
            if (!await PostSubagentStartAsync(sessionId, agentId, type ?? "subagent", transcript, ct)) continue;

            tracker.Started(child);
            Log(time, $"Grok subagent {agentId} ({type}) registered");
        }

        // Every tick, not only at registration: a child watcher that failed to spawn or exited early is
        // restarted, and a failed launch leaves the parent watcher running.
        foreach (var (child, _) in tracker.Active()) {
            if (ct.IsCancellationRequested) return;

            var agentId = GrokSubagents.AgentId(child);
            var key     = $"{sessionId}-{agentId}";
            try {
                await watchers.EnsureWatcherRunning(key: key, transcriptPath: GrokSubagents.ChildTranscript(transcriptPath, child),
                    agentId: agentId, sessionIdOverride: sessionId, vendor: "grok");
                spawnedChildKeys.Add(key);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                Log(time, $"Grok subagent {agentId} child watcher failed to start: {ex.Message}");
            }
        }

        foreach (var (child, type) in tracker.AwaitingStop()) {
            if (ct.IsCancellationRequested) return;

            if (await new GrokSubagentTeardown(profiles, http, watchers, time).FinishAsync(sessionId, transcriptPath, child, type))
                tracker.Stopped(child);
        }
    }

    /// <summary>
    /// A Muse child writes its own <c>subagent/&lt;id&gt;/session.jsonl</c> beside the parent's log; the
    /// scan is the authority on which children exist. A child the parent log has not announced yet is
    /// held back for <see cref="MuseChildWatchers.AnnouncementGraceScans"/> scans, so its label is not
    /// frozen as the placeholder when the announcement is merely flushed late.
    /// </summary>
    async Task ScanMuseSubagents(
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            MuseChildWatchers   children,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> childLogs;
        try {
            childLogs = MuseSubagentDiscovery.EnumerateChildLogs(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (childLogs.Count == 0) return;

        IReadOnlyDictionary<string, MuseChildLabel>? labels = null;

        foreach (var childLog in childLogs) {
            if (ct.IsCancellationRequested) return;
            if (seen.Contains(childLog)) continue;

            var childSessionId = Path.GetFileName(Path.GetDirectoryName(childLog)!);
            if (!Guid.TryParse(childSessionId, out var childGuid)) continue;

            labels ??= MuseSubagentDiscovery.ResolveChildLabels(transcriptPath);
            if (children.ShouldHold(childLog, announced: labels.ContainsKey(childGuid.ToString("D")))) continue;

            var label = MuseSubagentDiscovery.LabelFor(childSessionId, labels);
            var key   = $"{sessionId}-{label.AgentId}";
            var now   = time.GetUtcNow();

            if (!children.ShouldAttemptSpawn(key, now)) continue;

            // Fail-closed: register before streaming; a refused registration is retried next tick.
            if (!children.IsRegistered(key)) {
                if (!await PostSubagentStartAsync(sessionId, label.AgentId, label.AgentType, childLog, ct)) continue;

                children.Registered(key);
            }

            await watchers.EnsureWatcherRunning(key: key, transcriptPath: childLog, agentId: label.AgentId,
                sessionIdOverride: sessionId, vendor: "muse");

            // EnsureWatcherRunning returns normally when it could not start one.
            if (!watchers.IsWatcherAlive(key)) {
                var delay = children.SpawnFailed(key, now);
                Log(time, $"Muse subagent {label.AgentId} child watcher did not start; retrying in {delay.TotalSeconds:0}s");
                continue;
            }

            seen.Add(childLog);
            spawnedChildKeys.Add(key);
            children.Spawned(key, childLog, time.GetUtcNow());

            Log(time, $"Muse subagent {label.AgentId} ({label.AgentType}) registered + child watcher spawned");
        }
    }

    /// <summary>
    /// Stops the child watcher of a Muse worker once the parent log reports its <c>result_ready</c>, and
    /// restarts one whose log grows afterwards. The stop is a SIGTERM, so the child runs its final drain
    /// first. Reminder children have no such record and run until the parent ends. The full-log read
    /// happens only while a result may be unhandled.
    /// </summary>
    async Task StopFinishedMuseChildren(
            string                sessionId,
            string                transcriptPath,
            IReadOnlyList<string> drained,
            MuseChildWatchers     children
        ) {
        if (drained.Any(l => l.Contains("subagent.control.result_ready", StringComparison.Ordinal))) children.RecheckPending = true;

        try {
            var reengagedAt = time.GetUtcNow();

            foreach (var key in children.Reengaged(File.GetLastWriteTimeUtc, reengagedAt)) {
                var started = false;

                try {
                    await watchers.EnsureWatcherRunning(key: key, transcriptPath: children.LogOf(key),
                        agentId: key[(sessionId.Length + 1)..], sessionIdOverride: sessionId, vendor: "muse");
                    started = watchers.IsWatcherAlive(key);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    Log(time, $"Muse subagent {key} child watcher restart failed: {ex.Message}");
                }

                if (started) {
                    children.SpawnSucceeded(key);
                    Log(time, $"Muse subagent {key} resumed; child watcher restarted");
                } else {
                    var delay = children.RestartFailed(key, reengagedAt);
                    Log(time, $"Muse subagent {key} child watcher did not restart; retrying in {delay.TotalSeconds:0}s");
                }
            }

            if (!children.RecheckPending) return;

            var ready = MuseSubagentDiscovery.ResolveResultReadyCounts(transcriptPath)
                .ToDictionary(kv => $"{sessionId}-{Guid.Parse(kv.Key):N}", kv => kv.Value, StringComparer.Ordinal);

            var stops = children.SelectStops(ready, time.GetUtcNow());
            if (stops.Count == 0) return;

            // Read before the kill: a write after the child's final drain then still advances it.
            foreach (var key in stops) children.RecordLogMtime(key, File.GetLastWriteTimeUtc(children.LogOf(key)));

            var stopped = await children.Stop(watchers, stops, ready);
            Log(time, $"Stopped {stopped} of {stops.Count} finished Muse child watcher(s)");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // best-effort — the parent's teardown still stops every spawned child
        }
    }

    async Task<bool> PostSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = GeminiSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// OpenCode fires no subagent hooks; its subagents live in its SQLite db, so the kcap
    /// plugin fetches each child session via the SDK and writes its <c>{info,parts}</c> JSONL
    /// into <c>&lt;cacheDir&gt;/&lt;parentSid&gt;/&lt;childSid&gt;.jsonl</c>. The parent watcher discovers
    /// those files (mirroring <see cref="ScanGeminiSubagents"/>): on first sight it registers
    /// the subagent (<c>subagent-start</c>, fail-closed) then spawns a detached child watcher
    /// that streams it with the canonical agentId (= childSid) → <c>AgentSubsession-*</c>, which
    /// lines up with the agentId the server surfaced from the parent's <c>task</c> tool call.
    /// Idempotent across ticks via <paramref name="seen"/>; deterministic server-side lifecycle
    /// ids make re-registration safe.
    /// </summary>
    async Task ScanOpenCodeSubagents(
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostOpenCodeSubagentStartAsync(sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await watchers.EnsureWatcherRunning(key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "opencode");
            spawnedChildKeys.Add($"{sessionId}-{agentId}");

            Log(time, $"OpenCode subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    async Task<bool> PostOpenCodeSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = OpenCodeSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Codex fires no subagent hooks; its collab subagents (Codex 0.146+, multi-agent v2) each
    /// fork into their OWN rollout in the shared <c>~/.codex/sessions</c> tree, linked back via
    /// the child <c>session_meta</c>'s <c>parent_thread_id</c> / <c>thread_source: "subagent"</c>.
    /// The parent watcher scans that tree (day dirs from the parent's date forward, one header
    /// read per new file — see <see cref="CodexSubagentDiscovery.EnumerateSubagentRollouts"/>):
    /// on first sight it registers the subagent (<c>subagent-start</c>, fail-closed) then spawns
    /// a detached child watcher streaming the child rollout under the child's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <c>seen</c>;
    /// deterministic server-side lifecycle ids make re-registration safe. Disk enumeration (not
    /// the parent's in-band <c>sub_agent_activity</c> events) so a restarted parent watcher
    /// still recovers already-spawned children.
    /// </summary>
    /// <summary>
    /// #550 companion to the codex-child reap ceiling: fires on first sight of a rollout and
    /// thereafter only when its mtime advances, recording the mtime it fired for — so a reaped
    /// child stays reaped while its rollout is quiet but is re-ensured the moment its subagent
    /// re-engages (the fresh watcher resumes from the server frontier). Pure for testability.
    /// </summary>
    internal static bool ChildRolloutAdvanced(Dictionary<string, DateTime> lastMtimes, string filePath, DateTime currentMtime) {
        if (lastMtimes.TryGetValue(filePath, out var prev) && prev == currentMtime) return false;

        lastMtimes[filePath] = currentMtime;

        return true;
    }

    async Task ScanCodexSubagents(
            string                       sessionId,
            string                       transcriptPath,
            HashSet<string>              seen,
            HashSet<string>              ruledOut,
            Dictionary<string, DateTime> rolloutMtimes,
            ICollection<string>          spawnedChildKeys,
            CancellationToken            ct
        ) {
        List<CodexSubagentDiscovery.SubagentRollout> subs;
        try {
            subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(transcriptPath, sessionId, ruledOut);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        IReadOnlyDictionary<string, string>? spawnCalls = null;

        foreach (var sub in subs) {
            if (ct.IsCancellationRequested) return;

            // Both gates evaluate before branching so first sight also records the mtime. An
            // already-seen rollout that hasn't grown keeps its watcher state as-is — alive and
            // streaming, or reaped-idle — rather than respawning a reaped child for a finished rollout.
            var advanced = ChildRolloutAdvanced(rolloutMtimes, sub.FilePath, File.GetLastWriteTimeUtc(sub.FilePath));
            var isNew    = seen.Add(sub.FilePath);

            if (!isNew && !advanced) continue;

            var childAgentId = sub.ChildDashlessId;
            var agentType    = CodexSubagentDiscovery.AgentTypeFrom(sub.AgentPath, sub.AgentNickname);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (isNew
             && !await PostCodexSubagentStartAsync(sessionId, childAgentId, agentType, sub.FilePath,
                    CodexSubagentDiscovery.SpawnCallFor(spawnCalls ??= CodexSubagentDiscovery.ReadSpawnCalls(transcriptPath), sub.AgentPath), ct)) {
                seen.Remove(sub.FilePath);
                continue;
            }

            await watchers.EnsureWatcherRunning(key: $"{sessionId}-{childAgentId}", transcriptPath: sub.FilePath,
                agentId: childAgentId, sessionIdOverride: sessionId, vendor: "codex");
            spawnedChildKeys.Add($"{sessionId}-{childAgentId}");

            if (isNew) Log(time, $"Codex subagent {childAgentId} ({agentType}) registered + child watcher spawned");
        }
    }

    async Task<bool> PostCodexSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, string? spawnCallId, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = CodexSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile, spawnCallId);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Overall budget for one live child-stop POST attempt (auth client creation + the POST
    /// itself). Must stay well inside <see cref="WatcherHeartbeat.Threshold"/> (20s): the
    /// main loop touches the heartbeat once per iteration and awaits this call inline, so a
    /// slow attempt on the default 30s retry budget could mark the watcher stale and churn
    /// it exactly at turn completion. Retries ride later loop iterations instead, on the
    /// <see cref="CodexSubagentStopRetryInterval"/> cadence.
    /// </summary>
    static readonly TimeSpan CodexSubagentStopPostBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum spacing between live child-stop POST attempts, so a persistent failure
    /// (outage, auth trouble) never hammers <c>/hooks/subagent-stop</c> at the loop's 1s
    /// cadence. Deliberately no terminal give-up: an auth refresh can heal a 401 and the
    /// parent-end teardown dedupes whatever this never manages to post.
    /// </summary>
    static readonly TimeSpan CodexSubagentStopRetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The live per-child stop, posted by the CHILD watcher itself once its turn
    /// completed and idled — the same wire shape <see cref="CodexSubagentTeardown"/> posts at
    /// parent end (which stays the backstop; the server dedupes the duplicate stop via its
    /// deterministic lifecycle event ids). Bounded by
    /// <see cref="CodexSubagentStopPostBudget"/>; failures are logged and reported to the
    /// caller, never thrown.
    /// </summary>
    async Task<bool> PostCodexSubagentStopAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var budgetCap = new CancellationTokenSource(CodexSubagentStopPostBudget, time);
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCap.Token);

            using var client  = await http.ForBackgroundAsync(budgetCts.Token);
            var       payload = CodexSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-stop", content, time, timeout: CodexSubagentStopPostBudget, ct: budgetCts.Token);

            if (!resp.IsSuccessStatusCode) {
                Log(time, $"Codex subagent {agentId} stop POST returned {(int)resp.StatusCode}; "
                  + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");
            }

            return resp.IsSuccessStatusCode;
        } catch (Exception ex) {
            Log(time, $"Codex subagent {agentId} stop POST failed: {ex.Message}; "
              + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");

            return false;
        }
    }

    /// <summary>
    /// Agent type for a child watcher's own stop payload, read from the child rollout's
    /// <c>session_meta</c> header — the child watcher isn't handed the type at spawn (only the
    /// parent's scan derives it), and <see cref="CodexSubagentDiscovery.ReadHeader"/> never
    /// throws, so a still-unreadable header just yields the generic fallback.
    /// </summary>
    static string ResolveCodexChildAgentType(string transcriptPath) {
        var (_, meta) = CodexSubagentDiscovery.ReadHeader(transcriptPath);

        return CodexSubagentDiscovery.AgentTypeFrom(meta.AgentPath, meta.AgentNickname);
    }

    /// <summary>
    /// The first probe waits for a root rather than spending its minute on none, since a short session can
    /// end before the next one and the final probe is skipped below the transcript threshold.
    /// </summary>
    internal static bool SecondaryProbeDue(WatchState state, DateTimeOffset now) =>
        state.SecondaryRoots is { Roots.Count: > 0 } && now - state.LastSecondaryProbe > TimeSpan.FromSeconds(60);

    static readonly TimeSpan SubagentCheckoutScanGap = TimeSpan.FromSeconds(10);

    internal static void ObserveSubagentCheckouts(WatchState state, TimeProvider time, bool final = false) {
        if (state is not { SubagentCheckouts: { } subagentCheckouts, SecondaryRoots: { } secondaryRoots }) return;

        var knownRoots = secondaryRoots.Roots.Count;
        subagentCheckouts.FeedInto(secondaryRoots, final);
        if (secondaryRoots.Roots.Count > knownRoots) Log(time, $"Probing secondary checkouts for a PR: {string.Join(", ", secondaryRoots.Roots)}");
    }

    /// <summary>
    /// Whitelist of vendor values accepted by the server's session-end route.
    /// Used to reject unexpected --vendor input before interpolating into the URL
    /// path (defence-in-depth against path traversal even though the CLI runs locally).
    /// </summary>
    static readonly HashSet<string> KnownVendors = new(StringComparer.Ordinal) { "claude", "codex", "copilot", "gemini", "kiro", "pi", "opencode", "antigravity", "cursor", "mistral-vibe", "grok", "muse" };

    /// <summary>
    /// Total time budget for the parent-exit session-end POST, client creation included. A server
    /// that has not yet seen this watcher's connection close waits up to 10s for a drain it already
    /// had before answering, so the budget outlasts that wait; an end that still misses it is spooled.
    /// </summary>
    static readonly TimeSpan ParentExitPostBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Task 13 — pure extraction of the end-synthesis suppression <see cref="RunWatch"/>
    /// applies to its own idle-ceiling exit. Cursor's end-of-session synthesis has exactly one
    /// owner (the <c>sessionEnd</c> hook, or the server-side lease-gated sweep as a backstop);
    /// unlike Codex/Antigravity, the watcher posting <c>session-end</c> itself on an idle-ceiling
    /// exit would race/duplicate whichever of those two eventually fires. Scoped to
    /// <paramref name="idleExit"/> specifically — Cursor's other exit paths (<c>StopWatcher</c>,
    /// parent-exit) still post normally through the same call site this guards.
    /// </summary>
    internal static bool CursorSuppressesEndPost(string vendor, bool idleExit) => vendor == "cursor" && idleExit;

    /// <summary>
    /// Whether the watcher posts the session's end itself. Whether the session had a real turn does not
    /// enter into it: the start hook registered the session before this watcher read a line, so a
    /// turnless one left without its end stays Live for good.
    /// </summary>
    internal static bool PostsOwnSessionEnd([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? endReason, string? agentId, bool cursorSuppressesEndPost) =>
        endReason is not null && agentId is null && !cursorSuppressesEndPost;

    /// <summary>A Copilot watcher's parent was found by name, so it being gone means the agent has stopped, not
    /// that a hook returned. Waiting for a re-resolve could not help: nothing above a watcher names the agent.</summary>
    internal static bool EndsWhenParentFoundDead(string vendor, string? agentId) => vendor == "copilot" && agentId is null;

    internal async Task<bool> PostSessionEndOnParentExitAsync(
            string             sessionId,
            string             transcriptPath,
            string?            cwd,
            string             vendor,
            RepositoryPayload? repository,
            string             reason = "parent_exited"
        ) {
        if (!KnownVendors.Contains(vendor)) {
            Log(time, $"Parent-exit session-end skipped: unknown vendor '{vendor}'");
            return false;
        }

        // Gemini fires no subagent-stop hook and the child watchers spawned in ScanGeminiSubagents
        // carry no parent-pid watchdog, so when the parent process dies WITHOUT the session-end
        // hook this is the only place that finalizes live subagents. Mirror the hook path: kill
        // each child watcher, drain its tail, POST subagent-stop — capped so a slow drain can't
        // block the watchdog's self-termination, and run BEFORE the session-end POST so
        // SubagentCompleted lands ahead of SessionEnded. No-op when none were spawned.
        if (vendor == "gemini") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => new GeminiSubagentTeardown(profiles, http, watchers, time).DrainAsync(sessionId, transcriptPath),
                    GeminiSubagentTeardown.DrainCap, time);

                if (!finalized) {
                    Log(time, "Parent-exit Gemini subagent teardown cap elapsed; "
                      + "unfinalized subagents recover via: kcap import --gemini");
                }
            } catch (Exception ex) {
                Log(time, $"Parent-exit Gemini subagent teardown failed: {ex.Message}");
            }
        }

        // Codex likewise fires no subagent-stop hook and its child watchers (spawned in
        // ScanCodexSubagents) carry no parent-pid watchdog, so this session-end synthesis —
        // which for Codex covers idle_timeout AND parent-exit, there being no session-end
        // hook — is the only place that finalizes them. Same shape as the Gemini teardown;
        // runs BEFORE the session-end POST so SubagentCompleted lands ahead of SessionEnded.
        // No-op when none were spawned.
        if (vendor == "codex") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => new CodexSubagentTeardown(profiles, http, watchers, time).DrainAsync(sessionId, transcriptPath),
                    CodexSubagentTeardown.DrainCap, time);

                if (!finalized) {
                    Log(time, "Parent-exit Codex subagent teardown cap elapsed; "
                      + "unfinalized subagents recover via: kcap import --codex");
                }
            } catch (Exception ex) {
                Log(time, $"Parent-exit Codex subagent teardown failed: {ex.Message}");
            }
        }

        // OpenCode likewise fires no subagent-stop hook (the plugin-written child files are
        // discovered + streamed by ScanOpenCodeSubagents, with no parent-pid watchdog on the
        // child watchers), so the parent exit is the only place that finalizes them. Same
        // shape as the Gemini teardown; runs BEFORE the session-end POST so SubagentCompleted
        // lands ahead of SessionEnded. No-op when none were spawned.
        if (vendor == "grok") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => new GrokSubagentTeardown(profiles, http, watchers, time).DrainAsync(sessionId, transcriptPath),
                    GrokSubagentTeardown.DrainCap, time);

                if (!finalized) Log(time, "Parent-exit Grok subagent teardown cap elapsed; unfinalized subagents recover via: kcap import --grok");
            } catch (Exception ex) {
                Log(time, $"Parent-exit Grok subagent teardown failed: {ex.Message}");
            }
        }

        if (vendor == "opencode") {
            // DrainAsync is self-bounding (per-step caps + a shared cleanup deadline + a hard
            // overall ceiling), so it needs no outer time cap here — wrapping it in one risked
            // cutting later children's SubagentCompleted. It attempts subagent-stop for every child
            // WITHIN the overall budget; under a pathological/huge child count it stops at the
            // ceiling and returns how many were left unfinalized (logged below — OpenCode has no
            // historical import to recover a missed stop).
            try {
                var unfinalized = await new OpenCodeSubagentTeardown(profiles, http, watchers, time).DrainAsync(sessionId, transcriptPath);
                if (unfinalized > 0) {
                    Log(time, $"Parent-exit OpenCode subagent teardown hit the {OpenCodeSubagentTeardown.OverallBudget.TotalSeconds:0}s ceiling; "
                      + $"{unfinalized} subagent(s) left without SubagentCompleted");
                }
            } catch (Exception ex) {
                Log(time, $"Parent-exit OpenCode subagent teardown failed: {ex.Message}");
            }
        }

        var endHook = ParentExitEndBody(sessionId, transcriptPath, cwd, reason, time.GetUtcNow(), HostedAgent.FromEnvironment());

        return await PostSessionEndPayloadAsync(sessionId, vendor, endHook, repository, "Parent-exit");
    }

    /// <summary>The end a hook handed over, as the watcher posts it. The run identity is only ever the one the hook
    /// named: the watcher's own can be another run's.</summary>
    internal static JsonObject HandedEndBody(
            string payload, string sessionId, string transcriptPath, DateTimeOffset now, out string? notJson) {
        JsonObject end;
        notJson = null;

        try {
            end = JsonNode.Parse(payload) as JsonObject ?? new JsonObject();
        } catch (JsonException ex) {
            notJson = ex.Message;
            end     = new JsonObject { ["reason"] = "other", ["ended_at"] = now.ToString("O") };
        }

        end["session_id"]      = sessionId;
        end["transcript_path"] = transcriptPath;

        return end;
    }

    /// <summary>The end a watcher posts for its own stop. The watcher inherits the environment of the agent
    /// whose hook spawned it, so a hosted agent's end names that agent and its daemon epoch.</summary>
    internal static JsonObject ParentExitEndBody(
            string sessionId, string transcriptPath, string? cwd, string reason, DateTimeOffset endedAt, HostedAgent hosted) {
        var end = new JsonObject {
            ["session_id"]      = sessionId,
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = cwd ?? "",
            ["hook_event_name"] = "session_end",
            ["reason"]          = reason,
            // Scopes the server's SessionEnded id to this run: a vendor with no session-end hook ends only
            // here, and a resumed session's second exit would otherwise dedupe against its first.
            ["ended_at"]        = endedAt.ToString("O"),
        };
        SessionEndIncarnation.Stamp(end, hosted);

        return end;
    }

    /// <summary>Only a Muse session watcher takes an end its SessionEnd hook handed over; a child watcher's
    /// lifecycle belongs to its parent, and no other vendor's hook hands one.</summary>
    internal static bool TakesHandedMuseEnd(string vendor, string? agentId) => vendor == "muse" && agentId is null;

    /// <summary>A Muse session watcher whose exit is not the session's: a live process is claimed for the
    /// session, so a resumed run owns it now. Other vendors keep ending on their own exit, as their hooks
    /// do not re-arm a watcher across a resume.</summary>
    internal static bool SessionRunsOnElsewhere(string vendor, string? agentId, int? liveClaimant) =>
        TakesHandedMuseEnd(vendor, agentId) && liveClaimant is not null;

    int? LiveClaimant(string sessionId) =>
        SessionId.Parse(sessionId) is { } session ? AgentSessions.OnThisMachine(config).LiveClaimant(session) : null;

    /// <summary>Only a request handed over for this watcher's server, or one that recorded none: this watcher posts to its
    /// own server, and one for another is left to that server's drain.</summary>
    MuseSessionEndRequest? ClaimHandedMuseEnd(string sessionId) =>
        MuseSessionEndRequest.TryClaim(_paths, sessionId, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), time,
            recorded => recorded is null || ServerIdentity.SameServer(recorded, Url));

    internal enum HandedEndOutcome { Posted, Dropped, Failed }

    /// <summary>Settles the handed end this watcher took, then every other one handed over before it exits, oldest
    /// first, each once: another run's end handed over while this one was claimed would otherwise wait for a watcher
    /// nobody arms. One that can be neither posted nor spooled is held aside so the pass goes on to the rest, then put
    /// back with a backoff for the spool drain's sweep to send later.</summary>
    internal async Task SettleHandedEndsAsync(
            string sessionId, string transcriptPath, string? cwd, string vendor, RepositoryPayload? repository, MuseSessionEndRequest first) {
        var failed = new List<MuseSessionEndRequest>();

        for (MuseSessionEndRequest? handed = first; handed is not null; handed = ClaimHandedMuseEnd(sessionId)) {
            if (HoldHandedEndForResume(handed, AgentHoldingEndForResume(handed, sessionId)) is { } resumingId) {
                Log(time, $"session end suppressed: agent {resumingId} is resuming");
                continue;
            }

            switch (await PostHandedSessionEndAsync(sessionId, transcriptPath, vendor, repository, handed)) {
                // The session carried on, so this watcher's stop must not leave it unwatched.
                case HandedEndOutcome.Dropped:
                    await ArmSuccessorAsync(sessionId, transcriptPath, cwd, vendor, LiveClaimant(sessionId));
                    break;

                case HandedEndOutcome.Failed:
                    if (!await RetryHandedEndAsync(sessionId, transcriptPath, vendor, repository, handed)) failed.Add(handed);
                    break;
            }
        }

        foreach (var handed in failed) MuseSessionEndRequest.Defer(handed, time);
        if (failed.Count > 0) Log(time, $"{failed.Count} handed session-end(s) could not be sent; left for the spool drain to retry");
    }

    /// <summary>Posts a handed end once more directly: true, with the claim released, when the server took it or it
    /// went into the spool; false leaves the claim held. The retry sends the handed end itself: one rebuilt from this
    /// watcher would name the watcher's run, which can be an earlier one. Not while the session still has spooled
    /// hooks: a direct post would overtake them, ending the session before its start replays.</summary>
    async Task<bool> RetryHandedEndAsync(
            string sessionId, string transcriptPath, string vendor, RepositoryPayload? repository, MuseSessionEndRequest handed) {
        if (new HookSpool(config, time, Url).HasBacklog(sessionId) || !KnownVendors.Contains(vendor)
         || !await PostSessionEndPayloadAsync(sessionId, vendor, HandedEndBody(handed.Payload, sessionId, transcriptPath, time.GetUtcNow(), out _), repository, "Handed"))
            return false;

        MuseSessionEndRequest.Release(handed.ClaimedPath);

        return true;
    }

    /// <summary>The run a successor watcher follows: the one the daemon's marker records, when the marker names the
    /// process the successor follows, and none otherwise, so it never carries this watcher's earlier run.</summary>
    internal static WatcherRun SuccessorRun(UserHome home, string sessionId, int? followedPid) =>
        MuseHostedSessionMarker.TryRead(home, sessionId) is { } marker && marker.IsOwnedBy(followedPid)
            ? new(marker.AgentId, marker.DaemonEpoch)
            : new(null, null);

    /// <summary>Spawns the watcher a resumed session is left without once this one exits; this watcher's
    /// pid file is already retired, so the spawn sees no live watcher. The successor follows the resumed
    /// process when one is claimed; a watcher cannot resolve it from its own ancestry.</summary>
    async Task ArmSuccessorAsync(string sessionId, string transcriptPath, string? cwd, string vendor, int? parentPid) {
        try {
            await watchers.EnsureWatcherRunning(sessionId, transcriptPath, agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: true, vendor: vendor, parentPid: parentPid,
                runsAs: SuccessorRun(home, sessionId, parentPid));
            Log(time, $"Armed a successor watcher for the resumed session{(parentPid is { } p ? $" following pid {p}" : "")}");
        } catch (Exception ex) {
            Log(time, $"Arming a successor watcher failed: {ex.Message}");
        }
    }

    /// <summary>Posts the end the hook would have, the way the hook would have: behind any spooled backlog
    /// for the session, and into the spool on a failure, so an outage never drops it. Posted once it is
    /// posted or durably spooled; Dropped when the session carried on after the end was handed over;
    /// Failed keeps the claim on disk.</summary>
    async Task<HandedEndOutcome> PostHandedSessionEndAsync(
            string                sessionId,
            string                transcriptPath,
            string                vendor,
            RepositoryPayload?    repository,
            MuseSessionEndRequest request
        ) {
        if (MuseSessionEndRequest.ResumedSince(_paths, sessionId, request.HandedAt)) {
            Log(time, "Handed session-end dropped: the session was resumed after its end was handed over");
            MuseSessionEndRequest.Release(request.ClaimedPath);

            return HandedEndOutcome.Dropped;
        }

        var endHook = HandedEndBody(request.Payload, sessionId, transcriptPath, time.GetUtcNow(), out var notJson);
        if (notJson is not null) Log(time, $"Handed session-end request is not JSON ({notJson}); posting a plain end instead");

        if (repository is not null) {
            endHook["repository"] = JsonNode.Parse(
                JsonSerializer.Serialize(repository, CapacitorJsonContext.Default.RepositoryPayload)
            );
        }

        var route   = $"session-end/{vendor}";
        var body    = endHook.ToJsonString();
        var spool   = new HookSpool(config, time, Url);
        var outcome = spool.HasBacklog(sessionId)
            ? (spool.Append(sessionId, route, body) ? HookPostOutcome.Spooled : HookPostOutcome.Skipped)
            : await new AgentHookPoster(config, profiles, http, watchers, time)
                .PostOrSpoolAsync(route, body, $"{vendor}-watch", spool, sessionId, route);

        Log(time, $"Handed session-end {outcome}");

        // A rejected payload drops, as every hook post's does; only an end neither sent nor kept is tried again.
        if (outcome is not (HookPostOutcome.Posted or HookPostOutcome.Spooled or HookPostOutcome.Failed)) return HandedEndOutcome.Failed;

        MuseSessionEndRequest.Release(request.ClaimedPath);
        return HandedEndOutcome.Posted;
    }

    /// <summary>True when the server accepted the end or it is spooled for the drain. A session with a spooled
    /// backlog gets its end spooled behind it, so a stranded start still reaches the server first.</summary>
    async Task<bool> PostSessionEndPayloadAsync(string sessionId, string vendor, JsonObject endHook, RepositoryPayload? repository, string what) {
        if (repository is not null) {
            endHook["repository"] = JsonNode.Parse(
                JsonSerializer.Serialize(repository, CapacitorJsonContext.Default.RepositoryPayload)
            );
        }

        var body  = endHook.ToJsonString();
        var spool = new HookSpool(config, time, Url);

        bool Spool(string why) {
            var spooled = spool.Append(sessionId, $"session-end/{vendor}", body);
            Log(time, $"{what} session-end {why}; {(spooled ? "spooled for the next drain" : "the spool write failed, so it is lost")}");

            return spooled;
        }

        if (spool.HasBacklog(sessionId)) return Spool("queued behind the session's spooled hooks");

        using var budgetCts = new CancellationTokenSource(ParentExitPostBudget, time);

        try {
            using var httpClient = await http.ForBackgroundAsync(budgetCts.Token);
            using var content    = new StringContent(body, Encoding.UTF8, "application/json");

            var url = $"{Url}/hooks/session-end/{vendor}";
            using var response = await httpClient.PostWithRetryAsync(url, content, time, timeout: ParentExitPostBudget, ct: budgetCts.Token);

            if (!response.IsSuccessStatusCode) {
                var code = (int)response.StatusCode;

                if (HookSpool.IsRetryable(code)) return Spool($"POST returned HTTP {code}");

                Log(time, $"{what} session-end POST returned HTTP {code}");
                return false;
            }

            Log(time, $"{what} session-end POST succeeded");

            try {
                var answer = await response.Content.ReadAsStringAsync(budgetCts.Token);
                var node   = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);

                if (node?["generate_whats_done"]?.GetValue<bool>() == true) {
                    watchers.SpawnWhatsDoneGenerator(sessionId, vendor);
                }
            } catch (Exception ex) {
                Log(time, $"{what} session-end response parse failed: {ex.Message}");
            }

            return true;
        } catch (OperationCanceledException) {
            return Spool($"POST timed out after {ParentExitPostBudget.TotalSeconds:F0}s");
        } catch (HttpRequestException ex) {
            return Spool($"POST failed: {ex.Message}");
        } catch (Exception ex) {
            Log(time, $"{what} session-end POST failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>The first prompt and first reply a title is generated from, read the way the watch loop reads them,
    /// for a generator that runs after the watcher is gone.</summary>
    internal static (string? UserText, string? AssistantText) ReadTitleContext(string transcriptPath, string vendor, TimeProvider time) {
        var state       = new WatchState();
        var parseErrors = new OnceLog(message => Log(time, message));

        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line && (state.FirstUserText is null || state.FirstAssistantText is null)) {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (state.FirstUserText is null && TranscriptLineText.TryExtractUserText(line, vendor, parseErrors) is { } userText) TranscriptLineText.SetFirstUserText(state, userText, message => Log(time, message));

            if (state.FirstAssistantText is null && TranscriptLineText.TryExtractAssistantText(line, vendor) is { } assistantText)
                state.FirstAssistantText = assistantText.Length > 300 ? assistantText[..300] : assistantText;
        }

        return (state.FirstUserText, state.FirstAssistantText);
    }

    /// <summary>
    /// Links subagents from the parent transcript's INVOKE_SUBAGENT steps (the
    /// spawn-time signal), replacing the messages/*.json scan. For each child id not already
    /// posted, POST the link once via <paramref name="post"/> (returns true on success); a failed
    /// POST is left un-posted so a later scan retries. Pure over its inputs for unit testing.
    /// </summary>
    internal static async Task ExtractAndPostSubagentLinks(
            IEnumerable<string> lines, HashSet<string> posted, Func<string, Task<bool>> post, TimeProvider time) {
        foreach (var line in lines) {
            var children = AntigravitySubagents.ChildConversationIdsFromLine(line);
            if (children.Count == 0) {
                if (AntigravitySubagents.IsInvokeSubagentLine(line))
                    Log(time, "Antigravity INVOKE_SUBAGENT step had no parseable child conversationId (format drift?)");
                continue;
            }
            foreach (var child in children) {
                if (posted.Contains(child)) continue;
                if (await post(child)) posted.Add(child);   // fail-open: leave un-posted to retry
            }
        }
    }

    /// <summary>
    /// Live subagent nesting: links a subagent from its parent transcript's
    /// INVOKE_SUBAGENT steps — the spawn-time signal — rather than waiting for the child to
    /// report back. <paramref name="drainedLines"/> are the lines the watcher just drained from
    /// the parent transcript this tick. Each newly-seen child is POSTed to
    /// <c>/hooks/antigravity/subagent-link</c> and only added to <paramref name="posted"/> on
    /// success, so a failed POST retries on the next scan (fail-open — never breaks the drain
    /// loop).
    /// </summary>
    Task ScanAntigravitySubagentLinks(
            string sessionId, IReadOnlyList<string> drainedLines,
            HashSet<string> posted, CancellationToken ct) =>
        ExtractAndPostSubagentLinks(drainedLines, posted,
            child => PostAntigravitySubagentLinkAsync(sessionId, child, ct), time);

    async Task<bool> PostAntigravitySubagentLinkAsync(
        string sessionId, string childId, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = new JsonObject {
                ["hook_event_name"] = "subagent-link",
                ["session_id"]      = sessionId,
                ["agent_id"]        = childId,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/antigravity/subagent-link", content, time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Log(time, $"Antigravity subagent-link POST failed for child {childId}: {ex.Message}");
            return false;
        }
    }

    // An evidence-derived repo (from RepoEvidenceScanner, for a session launched outside any
    // checkout) has no cwd to re-probe, so a periodic cwd-based refresh finding nothing must
    // never be allowed to clear it back to null.
    internal static bool ShouldReplaceRepository(RepositoryPayload? detected, bool repositoryFromEvidence) =>
        detected is not null || !repositoryFromEvidence;

    /// <summary>
    /// Probes every checkout the agent mutated outside its launch cwd and links each PR found
    /// there to the session. The PR is linked under the repository its URL names, not the
    /// checkout's origin: in a fork checkout <c>gh pr view</c> resolves the base repository's PR
    /// while origin names the fork. A PR is posted once per (owner, repo, number); a failed post
    /// is retried on the next pass. Only github.com PRs are linked: the server endpoint rebuilds
    /// the remote URL from owner and repo on github.com, so any other host would hash to the
    /// wrong repository. Detection takes no cancellation token, so a cancelled pass abandons an
    /// in-flight probe (its child process dies on its own cap) rather than waiting for it. Each
    /// pass starts after the root the previous one last attempted, so a slow root cannot shadow
    /// the roots behind it every time, and <paramref name="beat"/> is called before every probe
    /// because the caller's heartbeat is otherwise untouched for the whole pass.
    /// </summary>
    internal static async Task LinkSecondaryPullRequestsAsync(
            WatchState                                          state,
            Func<string, TimeSpan, Task<RepositoryPayload?>>    detect,
            Func<RepositoryPayload, CancellationToken, Task<bool>> post,
            TimeSpan                                            budget,
            TimeProvider                                        time,
            CancellationToken                                   ct,
            Action                                              beat
        ) {
        if (state.SecondaryRoots is null || budget <= TimeSpan.Zero) return;

        var roots = state.SecondaryRoots.Roots.ToArray();
        if (roots.Length == 0) return;

        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var first = Math.Max(0, Array.IndexOf(roots, state.NextSecondaryRoot));

        for (var i = 0; i < roots.Length; i++) {
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || linked.IsCancellationRequested) return;

            // Advanced only once this root is actually attempted, so a root the check above
            // skips is the one the next pass starts with.
            var root = roots[(first + i) % roots.Length];
            state.NextSecondaryRoot = roots[(first + i + 1) % roots.Length];

            beat();

            RepositoryPayload? repo;
            try { repo = await detect(root, remaining).WaitAsync(linked.Token); }
            catch (OperationCanceledException) { return; }
            catch { repo = null; }

            if (repo is not { PrNumber: { } number, PrUrl: { } url }) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!PrRefParser.TryParse(url, out var owner, out var name, out var urlNumber) || urlNumber != number) continue;

            var key = (owner, name, number);
            if (state.LinkedPullRequests.Contains(key)) continue;

            bool accepted;
            try { accepted = await post(repo with { Owner = owner, RepoName = name }, linked.Token); } catch (OperationCanceledException) { return; }
            if (accepted) state.LinkedPullRequests.Add(key);
        }
    }

    Task LinkSecondaryPullRequestsAsync(WatchState state, string sessionId, TimeSpan budget, CancellationToken ct, Action beat) =>
        LinkSecondaryPullRequestsAsync(state,
            (root, remaining) => RepositoryDetection.DetectRepositoryAsync(router, config, root, time, remaining),
            (pr, token) => PostLinkedPullRequestAsync(sessionId, pr, "from a secondary checkout", token),
            budget, time, ct, beat);

    static readonly TimeSpan MaxCreatedPullRequestLookup = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Posts each created PR the server has not accepted yet, with its title and head from gh. A PR gh cannot
    /// describe waits for a later pass, since the server records a link without a branch as one on main; on the
    /// <paramref name="lastChance"/> pass it is linked by number and URL alone. The lookup gets at most half
    /// of what is left, so the post always has time, and a PR that fails moves behind the rest.
    /// </summary>
    internal static async Task LinkCreatedPullRequestsAsync(
            WatchState                                             state,
            Func<CreatedPullRequest, TimeSpan, Task<PrInfo?>>      describe,
            Func<RepositoryPayload, CancellationToken, Task<bool>> post,
            TimeSpan                                               budget,
            TimeProvider                                           time,
            CancellationToken                                      ct,
            bool                                                   lastChance,
            Action                                                 beat
        ) {
        if (state.PendingCreatedPullRequests.Count == 0 || budget <= TimeSpan.Zero) return;

        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        void MoveToBack(CreatedPullRequest pr) {
            state.PendingCreatedPullRequests.Remove(pr);
            state.PendingCreatedPullRequests.Add(pr);
        }

        foreach (var created in state.PendingCreatedPullRequests.ToArray()) {
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || linked.IsCancellationRequested) return;

            beat();

            var key = (created.Owner, created.RepoName, created.Number);

            if (!state.LinkedPullRequests.Contains(key)) {
                var lookup = remaining / 2 < MaxCreatedPullRequestLookup ? remaining / 2 : MaxCreatedPullRequestLookup;

                PrInfo? info;
                try { info = await describe(created, lookup).WaitAsync(lookup, time, linked.Token); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { MoveToBack(created); return; }
                catch { info = null; }

                if (info is null && !lastChance) {
                    MoveToBack(created);
                    continue;
                }

                var payload = new RepositoryPayload {
                    Host      = "github.com",
                    Owner     = created.Owner,
                    RepoName  = created.RepoName,
                    Branch    = info?.HeadRef,
                    PrNumber  = created.Number,
                    PrTitle   = info?.Title,
                    PrUrl     = created.Url,
                    PrHeadRef = info?.HeadRef,
                };

                bool accepted;
                try { accepted = await post(payload, linked.Token); } catch (OperationCanceledException) { MoveToBack(created); return; }

                if (!accepted) {
                    MoveToBack(created);
                    continue;
                }

                state.LinkedPullRequests.Add(key);
            }

            state.PendingCreatedPullRequests.Remove(created);
        }
    }

    Task LinkCreatedPullRequestsAsync(WatchState state, string sessionId, TimeSpan budget, CancellationToken ct, bool lastChance, Action beat) =>
        LinkCreatedPullRequestsAsync(state,
            (pr, lookup) => GitHubPrDetector.DescribeAsync(pr.Url, pr.Number, home.Path, lookup, RepositoryDetection.DefaultRunner(time)),
            (pr, token) => PostLinkedPullRequestAsync(sessionId, pr, "created in the session", token),
            budget, time, ct, lastChance, beat);

    // Half of WatcherHeartbeat.Threshold: the heartbeat is touched before each probe, so one
    // probe bounded by this budget is the longest the pass can leave it untouched. The pass is
    // retried every minute, so a lost post costs a minute, not the PR.
    static readonly TimeSpan SecondaryProbeBudget = TimeSpan.FromSeconds(10);

    // The watcher is killed 5s after it is told to stop. The final-line wait, the final drain and
    // the PR probe share this much of it, leaving the rest for the drain-complete signal.
    static readonly TimeSpan FinalSecondaryProbeDeadline = TimeSpan.FromSeconds(3);

    // A value the server has not settled is re-sent no sooner than this, however often the transcript drains.
    internal static readonly TimeSpan HarnessTitleRetryGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the store and sends what the tracker says is owed. Only a post the server took through
    /// <c>/hooks/harness-title</c> latches <see cref="WatchState.HarnessTitleSeen"/>. A post the server answered —
    /// recorded or refused on its merits — settles the value; anything else is re-sent after
    /// <see cref="HarnessTitleRetryGap"/>. The post runs inline in the watch loop, so it is bounded by
    /// <paramref name="budget"/> and <paramref name="beat"/> is called before it.
    /// </summary>
    internal static async Task PostHarnessTitleAsync(
            IHarnessTitleStore                                                        store,
            WatchState                                                                state,
            Func<HarnessTitlePost, TimeSpan, CancellationToken, Task<HarnessTitleOutcome>> post,
            TimeSpan                                                                  budget,
            TimeProvider                                                              time,
            Action                                                                    beat,
            Action<string>                                                            log,
            CancellationToken                                                         ct
        ) {
        var started = time.GetTimestamp();
        var tookUp  = state.HarnessTitleReadInFlight is not null;

        await PollHarnessTitleStoreAsync(store, state, post, budget, time, beat, log, ct);

        // A read taken up from an earlier poll may predate a later change (the final poll before exit is the last
        // chance to see one), so once it has been dealt with the store is read afresh while time remains.
        if (!tookUp || state.HarnessTitleReadInFlight is not null) return;

        await PollHarnessTitleStoreAsync(store, state, post, budget - time.GetElapsedTime(started), time, beat, log, ct);
    }

    static async Task PollHarnessTitleStoreAsync(
            IHarnessTitleStore                                                        store,
            WatchState                                                                state,
            Func<HarnessTitlePost, TimeSpan, CancellationToken, Task<HarnessTitleOutcome>> post,
            TimeSpan                                                                  budget,
            TimeProvider                                                              time,
            Action                                                                    beat,
            Action<string>                                                            log,
            CancellationToken                                                         ct
        ) {
        if (budget <= TimeSpan.Zero) return;

        // The read can be a synchronous SQLite query on a database the agent holds, so it runs off the loop and
        // spends the same budget as the post. One that outlasts the budget is left running and picked up by the next
        // poll, so a stuck store never has more than one read in flight (the readers are not safe to run concurrently).
        var started = time.GetTimestamp();
        var reading = state.HarnessTitleReadInFlight ??= StartRead(store, time);
        StoreTitle? read;
        try {
            read = await reading.Reading.WaitAsync(budget, time, ct);
        } catch (TimeoutException) {
            log("Harness title read did not finish within its budget");
            return;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            state.HarnessTitleReadInFlight = null;
            log($"Harness title read failed: {ex.Message}");
            return;
        }

        state.HarnessTitleReadInFlight = null;

        // Timed by the read's start, not its finish: a read carried over from an earlier poll must not date a later
        // change after something that happened while it ran.
        var now = time.GetUtcNow();
        if (state.TitleTracker!.Observe(read, reading.StartedAt) is not { } owed) return;

        if (ReferenceEquals(owed, state.LastHarnessTitleAttempted) && now - state.LastHarnessTitlePostAttempt < HarnessTitleRetryGap) return;

        var remaining = budget - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return;

        state.LastHarnessTitleAttempted   = owed;
        state.LastHarnessTitlePostAttempt = now;
        beat();

        HarnessTitleOutcome outcome;
        try {
            outcome = await post(owed, remaining, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            state.TitleTracker.OutcomeUnknown();
            throw;
        } catch (Exception ex) {
            state.TitleTracker.OutcomeUnknown();
            log($"Harness title post failed: {ex.Message}");
            return;
        }

        if (outcome is HarnessTitleOutcome.Posted) state.HarnessTitleSeen = true;
        if (outcome is HarnessTitleOutcome.Posted or HarnessTitleOutcome.PostedToLegacyRoute or HarnessTitleOutcome.Refused)
            state.TitleTracker.Settled(owed);
        else if (outcome is HarnessTitleOutcome.Failed) state.TitleTracker.OutcomeUnknown();
        if (outcome is not HarnessTitleOutcome.Posted) log($"Harness title not recorded: {outcome}");
    }

    // Observed from the start: a read no poll ever awaits again must not surface as an unobserved task exception.
    static HarnessTitleRead StartRead(IHarnessTitleStore store, TimeProvider time) {
        var startedAt = time.GetUtcNow();
        var reading   = Task.Run(store.Read, CancellationToken.None);
        reading.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new HarnessTitleRead(reading, startedAt);
    }

    Task PostHarnessTitleAsync(IHarnessTitleStore store, string sessionId, WatchState state, CancellationToken ct, Action beat,
            TimeSpan? budget = null) =>
        PostHarnessTitleAsync(store, state,
            async (owed, budget, token) => {
                // Client acquisition can refresh a credential over the network, so it spends the same budget.
                var       started  = time.GetTimestamp();
                using var deadline = new CancellationTokenSource(budget, time);
                using var linked   = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                using var client   = await http.ForBackgroundAsync(linked.Token);

                var remaining = budget - time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return HarnessTitleOutcome.Failed;

                return await HarnessTitleClient.PostOrFallBackAsync(client, time, Url, sessionId, owed, token, remaining);
            },
            budget ?? SecondaryProbeBudget, time, beat, message => Log(time, message), ct);

    async Task<bool?> ProbeHarnessTitlesAsync(CancellationToken ct, Action beat) {
        beat();
        try {
            var       started  = time.GetTimestamp();
            using var deadline = new CancellationTokenSource(SecondaryProbeBudget, time);
            using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var client   = await http.ForBackgroundAsync(linked.Token);

            var remaining = SecondaryProbeBudget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;

            return await HarnessTitleClient.ServerRecordsHarnessTitlesAsync(client, time, Url, remaining, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Log(time, $"Harness title probe failed: {ex.Message}");
            return null;
        }
    }

    async Task<bool> PostLinkedPullRequestAsync(string sessionId, RepositoryPayload pr, string source, CancellationToken ct) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = new JsonObject {
                ["owner"]       = pr.Owner,
                ["repo_name"]   = pr.RepoName,
                ["pr_number"]   = pr.PrNumber,
                ["pr_title"]    = pr.PrTitle,
                ["pr_url"]      = pr.PrUrl,
                ["pr_head_ref"] = pr.PrHeadRef,
                ["branch"]      = pr.Branch,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            // The server dedupes by (owner, repo, number), so retrying a throttled or 5xx status is safe.
            using var resp    = await client.PostWithRetryAsync(
                $"{Url}/api/sessions/{Uri.EscapeDataString(sessionId)}/pull-requests", content, time, ct: ct, retryStatuses: true);

            if (resp.IsSuccessStatusCode) Log(time, $"Linked {pr.Owner}/{pr.RepoName}#{pr.PrNumber} {source}");
            else Log(time, $"Linking {pr.Owner}/{pr.RepoName}#{pr.PrNumber} failed: HTTP {(int)resp.StatusCode}");

            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Log(time, $"Linking {pr.Owner}/{pr.RepoName}#{pr.PrNumber} failed: {ex.Message}");
            return false;
        }
    }

    static void Log(TimeProvider time, string message) =>
        Console.Error.WriteLine($"[{time.GetLocalNow():HH:mm:ss.fff}] [watch] {message}");

    public static int CountFileLines(string path) {
        try {
            if (!File.Exists(path)) {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;

            while (reader.ReadLine() is not null) {
                count++;
            }

            return count;
        } catch {
            return 0;
        }
    }

    /// <summary>
    /// Retries SignalR reconnection indefinitely with exponential backoff capped at 30s.
    /// The watcher is lightweight when idle — it should never give up while the session is active.
    /// </summary>
    sealed class InfiniteRetryPolicy : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount switch {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
    }
}
