using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Recording.Harness.Copilot;
using Capacitor.Cli.Workspace;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Detached post-<c>sessionEnd</c> finalizer for GitHub Copilot sessions: delivers the transcript tail,
/// then the session end.
/// </summary>
/// <remarks>
/// Copilot appends <c>session.shutdown</c>, the per-model input/cache token aggregate, and occasionally
/// the final assistant turn, to <c>events.jsonl</c> only after the <c>sessionEnd</c> hook starts, and on
/// quit kills the hook soon after. This process survives that kill and carries the hook's end. The server
/// watermark and deterministic ids make its drain idempotent.
/// </remarks>
sealed class CopilotFinalizeDrainCommand(
        ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, WatcherManager watchers, TimeProvider time) {

    // Outlasts the worst-case hook lifetime (PreHookDrainCap + Copilot's ~30s hook timeout), so it is still
    // polling when session.shutdown lands. Idle while polling and gone the instant it sees shutdown, so
    // the cap only bites when shutdown never comes: a crash, or a Copilot build that does not write it.
    static readonly TimeSpan DefaultPollBudget   = TimeSpan.FromSeconds(45);
    static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(300);

    // Past KillWatcher's 5s force-kill bound, so a watcher the hook is stopping has gone by then.
    static readonly TimeSpan DefaultWatcherStopGrace = TimeSpan.FromSeconds(8);

    // With the grace and KillWatcher's own 5s, these bound the work after the poll, and so how long the termination
    // signals stay ignored, to about 50s. A tail the cap cuts off is the server's to pull or `kcap import`'s.
    static readonly TimeSpan DefaultDrainCap = TimeSpan.FromSeconds(20);
    static readonly TimeSpan DefaultPostCap  = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Process entry point: <c>kcap copilot-finalize &lt;sessionId&gt; &lt;transcriptPath&gt; [endBody]</c>.
    /// </summary>
    public async Task<int> Run(string sessionId, string transcriptPath, string? endBody) {
        // The spawning hook closes our redirected std streams the instant it
        // starts us; writing to the dead pipe would fault. Redirect to a
        // per-session log file, mirroring WatchCommand.
        try {
            var logDir = config.Path("logs");
            Directory.CreateDirectory(logDir);
            var logWriter = new StreamWriter(Path.Combine(logDir, $"{sessionId}-finalize.log"), append: true) { AutoFlush = true };
            Console.SetOut(logWriter);
            Console.SetError(logWriter);
        } catch {
            // Logging is best-effort; never fail the drain because of it.
        }

        // Detach from the controlling terminal so closing the terminal right
        // after the session ends does not deliver SIGHUP before the tail lands.
        ProcessHelpers.DetachFromControllingTerminal();

        // Quitting VS Code terminates the processes it started about a second in, before Copilot has
        // written session.shutdown. The default action would drop the end this process carries, so the
        // signals are ignored until the end is posted or spooled.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Ignore);
        using var sighup  = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Ignore);

        await RunAsync(sessionId, transcriptPath, endBody, DefaultPollBudget, DefaultPollInterval, DefaultWatcherStopGrace,
            endDurable: () => {
                sigterm.Dispose();
                sighup.Dispose();
            });

        return 0;
    }

    void Ignore(PosixSignalContext context) {
        context.Cancel = true;
        Log($"{context.Signal} ignored; the end still has to be delivered");
    }

    /// <summary>
    /// Testable core: wait for the terminal <c>session.shutdown</c> line (or
    /// <paramref name="pollBudget"/>), deliver the tail, then end the session. No
    /// process detachment or log redirection, so tests drive it directly.
    /// </summary>
    internal async Task<bool> RunAsync(
            string   sessionId,
            string   transcriptPath,
            string?  endBody,
            TimeSpan pollBudget,
            TimeSpan pollInterval,
            TimeSpan watcherStopGrace,
            TimeSpan? drainCap   = null,
            TimeSpan? postCap    = null,
            Action?   endDurable = null
        ) {
        // The watcher the hook asked to stop, read before anything awaits: one found later follows a resumed run. The run's
        // spans were all opened by now too, which is what this end may close.
        var askedToStop = WatcherIncarnation(sessionId);
        var run         = new WorkspaceRunCutoff(CopilotRunBoundary.Latest(transcriptPath), time.GetUtcNow());

        var deadline    = time.GetUtcNow() + pollBudget;
        var sawShutdown = false;

        while (true) {
            if (LastLineIsShutdown(transcriptPath)) {
                sawShutdown = true;

                break;
            }

            if (time.GetUtcNow() >= deadline) {
                break;
            }

            try {
                await Task.Delay(pollInterval, time);
            } catch (OperationCanceledException) {
                break;
            }
        }

        Log(sawShutdown
            ? $"session.shutdown observed; draining tail for {sessionId}"
            : $"poll budget ({pollBudget.TotalSeconds:0}s) elapsed without session.shutdown; best-effort tail drain for {sessionId}");

        // Idempotent: resumes from the server watermark, and deterministic event ids dedupe what the watcher sent.
        if (!await TimeBudget.RunCappedAsync(() => watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "copilot"), drainCap ?? DefaultDrainCap, time))
            Log($"tail drain cap elapsed for {sessionId}; recoverable via: kcap import --copilot --session {sessionId}");

        var posted = await EndSessionAsync(sessionId, endBody, askedToStop, pollInterval, watcherStopGrace, postCap ?? DefaultPostCap);

        endDurable?.Invoke();

        // Only after an end the server took: a span must never outlive a lost end.
        if (posted) await new WorkspaceSpanHook(config, profiles, http, time).EndRunAsync(sessionId, run, WorkspaceSpanHook.EndBudget);

        // Detached, so it pays the span closes the drains ahead of hooks only recorded.
        await CopilotSpanClose.CloseDueAsync(WatcherPaths.FromEnvironment(config), new HookSpool(config, time, profiles.Resolution.ServerUrl), profiles.Resolution.ServerUrl, time, async (owed, owedRun) => {
            var spans = new WorkspaceSpanHook(config, profiles, http, time);
            await spans.EndRunAsync(owed, owedRun, WorkspaceSpanHook.EndBudget);
            return !spans.Holds(owed, owedRun);
        });

        return posted;
    }

    /// <summary>Delivers the end the hook spooled, or <paramref name="endBody"/> when it could not, and says whether the
    /// server has the session's end. An end it cannot deliver yet stays spooled for the next drain.</summary>
    async Task<bool> EndSessionAsync(string sessionId, string? endBody, string? askedToStop, TimeSpan pollInterval, TimeSpan watcherStopGrace, TimeSpan postCap) {
        const string route = "session-end/copilot";

        var spool = new HookSpool(config, time, profiles.Resolution.ServerUrl);

        if (endBody is null && !spool.HasBacklog(sessionId)) return false;

        // The watcher the hook asked to stop gets time to go, then is stopped outright, so the end cannot overtake lines it
        // has yet to send. A watcher that started since follows a resumed run, whose start supersedes a spooled end.
        var deadline = time.GetUtcNow() + watcherStopGrace;

        while (watchers.IsWatcherAlive(sessionId)) {
            if (time.GetUtcNow() >= deadline) {
                if (askedToStop is not null && WatcherIncarnation(sessionId) == askedToStop) {
                    Log($"the watcher of {sessionId} is still running; stopping it");
                    await watchers.KillWatcher(sessionId);
                }

                if (!watchers.IsWatcherAlive(sessionId)) break;

                if (endBody is not null) spool.Append(sessionId, route, endBody);
                Log($"session end for {sessionId} left spooled: a watcher is running");

                return false;
            }

            await Task.Delay(pollInterval, time);
        }

        // After the wait: the end would bring back the stream a person's disable deleted.
        if (DisabledSessions.IsDisabledByPerson(sessionId, config)) {
            Log($"session end not sent for {sessionId}: it was disabled");

            return false;
        }

        if (endBody is not null && !spool.HasBacklog(sessionId)) {
            var outcome = await PostWithinAsync(spool, sessionId, route, endBody, postCap);
            Log($"session end for {sessionId}: {outcome}");

            // As a drain would: the span close this end owes is then due, and a failed close is retried.
            if (outcome == HookPostOutcome.Posted) CopilotSpanClose.NoteOutcome(WatcherPaths.FromEnvironment(config), route, endBody, DrainOutcome.Delivered);

            return outcome == HookPostOutcome.Posted;
        }

        // A spooled start goes ahead of the end, and the drain keeps that order.
        if (endBody is not null) spool.Append(sessionId, route, endBody);

        // An empty spool proves nothing: a rejected end leaves it as well.
        DrainOutcome? ended = null;

        await new AgentHookPoster(config, profiles, http, watchers, time).DrainSpoolsNowAsync(spool, sessionId, (entryRoute, body, outcome) => {
            if (HookSpool.IsTerminalRoute(entryRoute) && SessionOf(body) == sessionId) ended = outcome;
        });

        Log($"session end for {sessionId}: {ended?.ToString() ?? "left spooled"}");

        return ended == DrainOutcome.Delivered;
    }

    // A post still in flight at the cap goes to the spool as well: the server derives the end's event id from its reason
    // and ended_at, so delivering this body twice records one end.
    async Task<HookPostOutcome> PostWithinAsync(HookSpool spool, string sessionId, string route, string endBody, TimeSpan cap) {
        var post = new AgentHookPoster(config, profiles, http, watchers, time)
            .PostOrSpoolAsync(route, endBody, "copilot-finalize", spool, sessionId, route);

        using var timer = new CancellationTokenSource();

        if (await Task.WhenAny(post, Task.Delay(cap, time, timer.Token)) == post) {
            await timer.CancelAsync();

            return await post;
        }

        _ = post.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        Log($"session end post for {sessionId} unanswered after {cap.TotalSeconds:0}s; spooling it");

        return spool.Append(sessionId, route, endBody) ? HookPostOutcome.Spooled : HookPostOutcome.Skipped;
    }

    string? WatcherIncarnation(string sessionId) {
        try {
            return File.ReadAllText(WatcherPaths.FromEnvironment(config).PidFile(sessionId)).Trim();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static string? SessionOf(string body) {
        try {
            return JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>
    /// True when the last non-blank line of the transcript is a Copilot
    /// <c>session.shutdown</c> envelope. Resume-safe: a prior run's shutdown sits
    /// mid-file (more events follow it), so only the genuine terminal shutdown
    /// makes this true. Fail-closed on missing / empty / malformed input.
    /// </summary>
    internal static bool LastLineIsShutdown(string transcriptPath) {
        try {
            return ReadLastLine(transcriptPath) is { } last && CopilotShutdown.IsShutdownLine(last);
        } catch {
            return false;
        }
    }

    /// <summary>True only when the transcript was read and ends on something other than a shutdown. Failing to read
    /// it proves nothing, so a caller that already saw a shutdown keeps that answer.</summary>
    internal static bool MovedPastShutdown(string transcriptPath) {
        try {
            return ReadLastLine(transcriptPath) is { } last && !CopilotShutdown.IsShutdownLine(last);
        } catch {
            return false;
        }
    }

    static string? ReadLastLine(string transcriptPath) {
        if (!File.Exists(transcriptPath)) return null;

        string? lastNonBlank = null;

        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) lastNonBlank = line;
        }

        return lastNonBlank;
    }

    void Log(string message) =>
        Console.Error.WriteLine($"[{time.GetLocalNow():HH:mm:ss.fff}] [copilot-finalize] {message}");
}
