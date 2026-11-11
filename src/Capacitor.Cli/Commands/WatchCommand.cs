using Capacitor.Cli.Recording;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Copilot;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Recording.Harness.Claude;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Workspace;

namespace Capacitor.Cli.Commands;

class WatchCommand(
        ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses,
        ICapacitorHttpClient http, ICredentialSource credentials, WatcherManager watchers,
        GitProviderRouter router, TimeProvider time, AccountStore accounts, UserHome home, CapacitorServer server) {
    readonly WatcherPaths _paths = WatcherPaths.FromEnvironment(config);

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

    /// <summary>What a resume holds a handed end for, when it does: the agent its hook named, or the session.</summary>
    internal static string? AgentHoldingEndForResume(MuseSessionEndRequest handed, string sessionId) =>
        HandedEndHeld(handed.Payload, sessionId, h => SessionEndFence.ShouldHold(h, sessionId), SessionEndFence.ShouldHold);

    /// <summary>A handed end is judged as the run its hook named, never as the watcher's: a successor watcher can
    /// carry another run's identity. An end that names no run is placed by its session, as an end with no epoch.</summary>
    internal static string? HandedEndHeld(
            string payload, string sessionId, Func<HostedAgent, bool> holdsRun, Func<ExitedSession, bool> holdsSession) {
        if (HandedEndRun(payload) is { } run)
            return SessionEnd.DecideSessionEnd(runsOnElsewhere: false, run, holdsRun) == SessionEndAction.HeldForResume ? run.AgentId : null;

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
        var stopRequested = 0;
        // The run the transcript named when a stop arrived, and when; what follows belongs to whatever runs next.
        string? stopRunId   = null;
        var     stopNoted   = 0;
        long    stopTicks   = 0;
        // set by the staged parent-dead recovery loop when it ends a wedged watcher on the ceiling.
        var wedgedCeilingExit = 0;
        // set when Kiro Crew is finished with a session its still-running kiro-cli process hosts.
        var crewFinished = 0;
        var armDeadParentOnConnect = false;

        // Read by whatever decides how the session ended: a server stop sets the recorder's token a moment before cts
        // hears of it, so a watch that fires in between would report an end the stop already owns.
        var stopping = CancellationToken.None;

        var source       = new RecordingSource(sessionId, agentId, transcriptPath, vendor, cwd);
        var host         = new WatchRecorderHost(config, profiles, harnesses, http, watchers, time, ArmProcessWatches, cts.Cancel);
        var titlesAllowed = server.LocalRoot is not { } localRoot || LocalIntelligenceGate.For(config, localRoot).Allows(Url);
        if (!skipTitle && !titlesAllowed) Log(time, $"Not generating a title: {LocalIntelligenceGate.NotSetUp}");

        var housekeeping = new RecorderHousekeeping(source, skipTitle || !titlesAllowed, parentPid, host, config, profiles, harnesses, http, router, time, accounts, home,
            message => Log(time, message), TouchHeartbeat);

        var hubConnection = BuildHubConnection($"{Url}/hubs/sessions");
        var transport     = new HubRecorderTransport(hubConnection, source.Key, TouchHeartbeat, message => Log(time, message), time);

        using var recorder = new TranscriptRecorder(source, transport, new NoStamps(), time, new RecorderContext(
            server, housekeeping, time, message => Log(time, message), TouchHeartbeat));

        var state = recorder.State;

        stopping = recorder.Stopping;

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
        void NoteStopRequest() {
            Interlocked.Exchange(ref stopRequested, 1);

            if (Interlocked.Exchange(ref stopNoted, 1) != 0) return;

            Interlocked.Exchange(ref stopTicks, time.GetUtcNow().UtcTicks);
            if (vendor == "copilot") Volatile.Write(ref stopRunId, CopilotRunBoundary.LatestIdOf(recorder.TranscriptPath));
        }

        // The run this watcher recorded: the one the transcript named when the stop arrived, or names now when the run was
        // found over some other way. Unreadable, the spans opened by the stop, or by now.
        WorkspaceRunCutoff RunCutoff() {
            var ticks = Interlocked.Read(ref stopTicks);

            return new WorkspaceRunCutoff(
                ticks > 0 ? Volatile.Read(ref stopRunId) : CopilotRunBoundary.LatestIdOf(recorder.TranscriptPath),
                ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : time.GetUtcNow());
        }

        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            NoteStopRequest();
            recorder.RequestStop();
        };
        using var sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
            NoteStopRequest();
            recorder.RequestStop();
            ctx.Cancel = true;
        });
        using var stopRequest = WatcherStopSignal.Listen(Environment.ProcessId, () => {
            Log(time, "Stop requested; shutting down watcher");
            NoteStopRequest();
            recorder.RequestStop();
        });

        // Quitting VS Code terminates the watchers it started, and a session whose sessionEnd hook Copilot skipped has
        // nothing else to send its end. Any other owner's end may arrive too: the server keeps a session's first.
        var copilotSession = vendor == "copilot" && agentId is null;

        bool CopilotStopEndApplies() =>
            copilotSession && Volatile.Read(ref stopRequested) == 1 && !DisabledSessions.IsDisabledByPerson(sessionId, config);

        // The end is held before the wait, which the teardown that stopped this watcher can cut short; a later spool
        // drain sends it once the runtime has gone or the transcript ends on its shutdown line.
        async Task<string?> CopilotStopEndAsync(string path) {
            var held     = SessionEnd.ParentExitEndBody(sessionId, path, cwd, "parent_exited", time.GetUtcNow(), HostedAgent.FromEnvironment());
            var holding  = CopilotPendingEnd.WriteWithRetryAsync(_paths, sessionId, held.ToJsonString(), Url, parentPid, path, time, HeldEndWriteWindow,
                RunCutoff().RunId, Environment.ProcessId);

            var reason = await CopilotStopRequestEnd.DecideAsync(
                () => parentPid is { } runtime && !ProcessHelpers.IsProcessAlive(runtime),
                () => CopilotFinalizeDrainCommand.LastLineIsShutdown(path),
                time, CopilotStopRequestEnd.Wait);

            // A premature first end is permanent, where a lost one is the server's stale sweep's to recover, so an end that
            // could not be held for a session not shown over is left unsent.
            var isHeld = await holding;
            Log(time, reason is not null ? $"Stopped with the Copilot session over; ending it ({reason})"
                : isHeld                 ? "Stopped before the Copilot session was over; its end is held for a later spool drain"
                                         : "Stopped before the Copilot session was over and its end could not be held; leaving it to the server");

            return reason;
        }

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
                        if (stopping.IsCancellationRequested) return;

                        Log(time, $"Parent pid {ppid} exited; shutting down watcher");
                        Interlocked.Exchange(ref parentExited, 1);
                        cts.Cancel();

                        return;
                    }
                }
            }, cts.Token);
        }

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
                                    if (stopping.IsCancellationRequested) return;

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
        void ArmProcessWatches() {
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
                            if (stopping.IsCancellationRequested) return;

                            Log(time, "Kiro Crew is finished with this session; shutting down watcher");
                            Interlocked.Exchange(ref crewFinished, 1);
                            cts.Cancel();

                            return;
                        }
                    }
                }, cts.Token);
            }
        }

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
            var earlyRun = RunCutoff();
            await hubConnection.DisposeAsync();

            // A watcher stopped before it connected is still the one the Muse hook may have handed an
            // end to: it was alive when the hook looked. Nothing has been streamed yet, so the log is
            // drained inline ahead of the end, as the hook would have.
            if (TakesHandedMuseEnd(vendor, agentId)) {
                watchers.RemoveOwnPidFile(sessionId, Environment.ProcessId);

                if (MuseSessionEndRequest.ClaimHandedEnd(_paths, sessionId, Url, time) is { } handed) {
                    await watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: vendor);
                    await SettleHandedEndsAsync(sessionId, transcriptPath, cwd, vendor, repository: null, handed);
                }
            }

            if (CopilotStopEndApplies() && await CopilotStopEndAsync(transcriptPath) is { } early) {
                watchers.RemoveOwnPidFile(sessionId, Environment.ProcessId);
                await PostCopilotEndAsync(sessionId, transcriptPath, cwd, repository: null, early, earlyRun);
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

        // Before the final drain and any post or wait, each of which a replacement run can start during.
        var run = RunCutoff();

        await recorder.FinishAsync(CancellationToken.None);

        transcriptPath = recorder.TranscriptPath;

        // Stop spawned child watchers on every parent exit path, SIGTERM-first, so each runs its final
        // drain before the session-end post below. A SIGKILLed parent skips this, and the codex-child
        // reap ceiling backstops that orphan.
        if (housekeeping.SpawnedChildKeys.Count > 0) {
            Log(time, $"Stopping {housekeeping.SpawnedChildKeys.Count} spawned child watcher(s)");
            await watchers.KillWatchers(housekeeping.SpawnedChildKeys);
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
        var endReason = SessionEnd.DecideEndReason(
            parentExited:  Volatile.Read(ref parentExited)      == 1,
            crewFinished:  Volatile.Read(ref crewFinished)      == 1,
            wedgedCeiling: Volatile.Read(ref wedgedCeilingExit) == 1,
            idle:          recorder.EndedIdle,
            // Read again: a resume can land after the grace ran out, and the final drain does not always run.
            copilotShutdown: recorder.EndedOnCopilotShutdown && !CopilotFinalizeDrainCommand.MovedPastShutdown(transcriptPath));

        if (endReason is null && CopilotStopEndApplies()) endReason = await CopilotStopEndAsync(transcriptPath);

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

        var museEndRequest = housekeeping.HandedMuseEnd;

        if (TakesHandedMuseEnd(vendor, agentId)) museEndRequest ??= MuseSessionEndRequest.ClaimHandedEnd(_paths, sessionId, Url, time);

        if (museEndRequest is { } handedEnd) {
            await SettleHandedEndsAsync(sessionId, transcriptPath, cwd, vendor, state.Repository, handedEnd);
        } else if (SessionEnd.PostsOwnSessionEnd(endReason, agentId, cursorSuppressesEndPost)) {
            // A resumed session's hooks claim its new process; the end of the one this watcher followed
            // is then not the session's end, and the new process is left without a watcher.
            var claimant = LiveClaimant(sessionId);
            var hosted   = HostedAgent.FromEnvironment();

            switch (SessionEnd.DecideSessionEnd(SessionRunsOnElsewhere(vendor, agentId, claimant), hosted, h => SessionEndFence.ShouldHold(h, sessionId))) {
                case SessionEndAction.RunsOnElsewhere:
                    Log(time, $"Not posting a {endReason} session-end: the session runs on in pid {claimant}");
                    await ArmSuccessorAsync(sessionId, transcriptPath, cwd, vendor, claimant);
                    break;
                case SessionEndAction.HeldForResume:
                    Log(time, $"session end suppressed: agent {hosted?.AgentId} is resuming");
                    break;
                default:
                    if (copilotSession) await PostCopilotEndAsync(sessionId, transcriptPath, cwd, state.Repository, endReason, run);
                    else await PostSessionEndOnParentExitAsync(sessionId, transcriptPath, cwd, vendor, state.Repository, endReason);
                    break;
            }
        }

        await logWriter.DisposeAsync();

        return 0;
    }

    // Within the 5 s KillWatcher gives a stopping watcher before it force-kills.
    static readonly TimeSpan HeldEndWriteWindow = TimeSpan.FromSeconds(4);

    /// <summary>Posts a Copilot watcher's own end, after the spool's: the server keeps a session's first end, and a sessionEnd
    /// hook's spooled one carries the quit time. An end this watcher held is then no longer owed.</summary>
    /// <param name="run">The run this watcher recorded, taken before any wait or post: a replacement run's spans are not
    /// this end's to close.</param>
    internal async Task PostCopilotEndAsync(string sessionId, string transcriptPath, string? cwd, RepositoryPayload? repository, string reason, WorkspaceRunCutoff run) {
        await new AgentHookPoster(config, profiles, http, watchers, time).DrainSpoolsNowAsync(new HookSpool(config, time, Url), sessionId);

        // Recording may have been turned off during the stop wait or the drain, and the end carries the session's paths.
        if (DisabledSessions.IsDisabledByPerson(sessionId, config)) {
            CopilotPendingEnd.Discard(_paths, sessionId, ownedBy: Environment.ProcessId);
            return;
        }

        if (await PostSessionEndOnParentExitAsync(sessionId, transcriptPath, cwd, "copilot", repository, reason, run))
            CopilotPendingEnd.Discard(_paths, sessionId, ownedBy: Environment.ProcessId);
    }

    /// <summary>
    /// Whitelist of vendor values accepted by the server's session-end route.
    /// Used to reject unexpected --vendor input before interpolating into the URL
    /// path (defence-in-depth against path traversal even though the CLI runs locally).
    /// </summary>
    static readonly HashSet<string> KnownVendors = new(StringComparer.Ordinal) { "claude", "codex", "copilot", "gemini", "kiro", "pi", "opencode", "antigravity", "cursor", "mistral-vibe", "grok", "muse" };

    static readonly TimeSpan WorkspaceEndCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The end-synthesis suppression <see cref="RunWatch"/>
    /// applies to its own idle-ceiling exit. Cursor's end-of-session synthesis has exactly one
    /// owner (the <c>sessionEnd</c> hook, or the server-side lease-gated sweep as a backstop);
    /// unlike Codex/Antigravity, the watcher posting <c>session-end</c> itself on an idle-ceiling
    /// exit would race/duplicate whichever of those two eventually fires. Scoped to
    /// <paramref name="idleExit"/> specifically — Cursor's other exit paths (<c>StopWatcher</c>,
    /// parent-exit) still post normally through the same call site this guards.
    /// </summary>
    internal static bool CursorSuppressesEndPost(string vendor, bool idleExit) => vendor == "cursor" && idleExit;

    /// <summary>A Copilot watcher's parent was found by name, so it being gone means the agent has stopped, not
    /// that a hook returned. Waiting for a re-resolve could not help: nothing above a watcher names the agent.</summary>
    internal static bool EndsWhenParentFoundDead(string vendor, string? agentId) => vendor == "copilot" && agentId is null;

    internal async Task<bool> PostSessionEndOnParentExitAsync(
            string             sessionId,
            string             transcriptPath,
            string?            cwd,
            string             vendor,
            RepositoryPayload? repository,
            string             reason = "parent_exited",
            WorkspaceRunCutoff? run        = null
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

        var endHook = SessionEnd.ParentExitEndBody(sessionId, transcriptPath, cwd, reason, time.GetUtcNow(), HostedAgent.FromEnvironment());
        var posted  = await PostSessionEndPayloadAsync(sessionId, vendor, endHook, repository, "Parent-exit");

        // After the end, which the watcher's short stop window protects, and only once it was posted or spooled:
        // a vendor without turn hooks opened its span at SessionStart, and one with them may have died mid-turn.
        // Its drain is bounded, and is the last chance a session with no other hook gets.
        // A run cutoff keeps this end to its own run's spans, should a later run already have opened one.
        if (posted) {
            var spans = new WorkspaceSpanHook(config, profiles, http, time);
            await (run is not null ? spans.EndRunAsync(sessionId, run, WorkspaceEndCap) : spans.EndAsync(sessionId, WorkspaceEndCap));
        }

        return posted;
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

    internal enum HandedEndOutcome { Posted, Dropped, Failed }

    /// <summary>Settles the handed end this watcher took, then every other one handed over before it exits, oldest
    /// first, each once: another run's end handed over while this one was claimed would otherwise wait for a watcher
    /// nobody arms. One that can be neither posted nor spooled is held aside so the pass goes on to the rest, then put
    /// back with a backoff for the spool drain's sweep to send later.</summary>
    internal async Task SettleHandedEndsAsync(
            string sessionId, string transcriptPath, string? cwd, string vendor, RepositoryPayload? repository, MuseSessionEndRequest first) {
        var failed = new List<MuseSessionEndRequest>();

        for (MuseSessionEndRequest? handed = first; handed is not null; handed = MuseSessionEndRequest.ClaimHandedEnd(_paths, sessionId, Url, time)) {
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

    async Task<bool> PostSessionEndPayloadAsync(string sessionId, string vendor, JsonObject endHook, RepositoryPayload? repository, string what) =>
        await new SessionEndPoster(config, Url, http, time, message => Log(time, message), (session, endVendor) => watchers.SpawnWhatsDoneGenerator(session, endVendor))
            .PostAsync(sessionId, vendor, endHook, repository, what) is SessionEndDelivery.Delivered;

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
