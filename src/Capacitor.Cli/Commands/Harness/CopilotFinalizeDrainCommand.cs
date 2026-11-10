using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Detached post-<c>sessionEnd</c> finalizer for GitHub Copilot sessions: delivers the transcript tail,
/// then the session end.
/// </summary>
/// <remarks>
/// Copilot appends <c>session.shutdown</c>, the per-model input/cache token aggregate, and occasionally
/// the final assistant turn, to <c>events.jsonl</c> only after the <c>sessionEnd</c> hook starts. On quit
/// its runtime stops about 2 seconds after that, killing the hook, so the hook's own session-end POST
/// (which follows a capped drain) can never be relied on. This process survives that kill. The server
/// watermark and deterministic ids make its drain idempotent, and its end is the hook's own body, which
/// the server records once whichever arrives first.
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

        await RunAsync(sessionId, transcriptPath, endBody, DefaultPollBudget, DefaultPollInterval, DefaultWatcherStopGrace);

        return 0;
    }

    /// <summary>
    /// Testable core: wait for the terminal <c>session.shutdown</c> line (or
    /// <paramref name="pollBudget"/>), deliver the tail, then end the session. No
    /// process detachment or log redirection, so tests drive it directly.
    /// </summary>
    internal async Task RunAsync(
            string   sessionId,
            string   transcriptPath,
            string?  endBody,
            TimeSpan pollBudget,
            TimeSpan pollInterval,
            TimeSpan watcherStopGrace
        ) {
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

        // Idempotent: resumes from the server watermark; deterministic event ids
        // dedupe anything the hook's inline-drain already delivered.
        await watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "copilot");

        if (endBody is not null) await EndSessionAsync(sessionId, endBody, pollInterval, watcherStopGrace);
    }

    async Task EndSessionAsync(string sessionId, string endBody, TimeSpan pollInterval, TimeSpan watcherStopGrace) {
        // The hook stops the watcher when it starts this process, and a watcher stopped that way sends no end,
        // so it gets time to go. One still running after that is a resumed run this end would close, or one
        // the hook died before stopping, whose parent-exit path sends the end itself.
        var deadline = time.GetUtcNow() + watcherStopGrace;

        while (watchers.IsWatcherAlive(sessionId)) {
            if (time.GetUtcNow() >= deadline) {
                Log($"session end not sent for {sessionId}: a watcher is running");

                return;
            }

            await Task.Delay(pollInterval, time);
        }

        // After the wait: the end would bring back the stream a person's disable deleted.
        if (DisabledSessions.IsDisabledByPerson(sessionId, config)) {
            Log($"session end not sent for {sessionId}: it was disabled");

            return;
        }

        const string route = "session-end/copilot";

        var spool = new HookSpool(config, time, profiles.Resolution.ServerUrl);

        // A spooled start must reach the server before its end does.
        var outcome = spool.HasBacklog(sessionId)
            ? spool.Append(sessionId, route, endBody) ? HookPostOutcome.Spooled : HookPostOutcome.Skipped
            : await new AgentHookPoster(config, profiles, http, watchers, time)
                .PostOrSpoolAsync(route, endBody, "copilot-finalize", spool, sessionId, route);

        Log($"session end for {sessionId}: {outcome}");
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
