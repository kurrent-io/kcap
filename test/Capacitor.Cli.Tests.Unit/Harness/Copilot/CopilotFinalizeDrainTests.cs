using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Pure detection logic for the Copilot finalize drain: the drainer
/// treats a terminal <c>session.shutdown</c> line as "the tail is complete".
/// </summary>
public class CopilotFinalizeDrainLastLineTests {
    const string ShutdownLine =
        """{"type":"session.shutdown","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":52320,"cacheReadTokens":34736,"cacheWriteTokens":17579}}}},"id":"evt-shutdown","timestamp":1234567890}""";

    const string AssistantLine =
        """{"type":"assistant.message","data":{"content":"all done"},"id":"evt-asst","timestamp":1234567000}""";

    static string WriteTemp(TempDir tmp, string contents) =>
        tmp.CreateFile("events.jsonl", contents);

    [Test]
    public async Task True_WhenTerminalLineIsShutdown() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + ShutdownLine + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsTrue();
    }

    [Test]
    public async Task True_WithTrailingBlankLines() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + ShutdownLine + "\n\n   \n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsTrue();
    }

    [Test]
    public async Task False_WhenTerminalLineIsOtherType() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, ShutdownLine + "\n" + AssistantLine + "\n");

        // Resume-safety: a prior run's shutdown sits mid-file with later events
        // after it; only the genuine terminal shutdown should trip detection.
        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }

    [Test]
    public async Task False_ForMissingFile() {
        using var pathDir = TempDir.WithPathTo("kcap_missing.jsonl", out var path);

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }

    [Test]
    public async Task False_ForMalformedTerminalLine() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + "{not json" + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }
}

/// <summary>
/// End-to-end timing behaviour of the Copilot finalize drain, driven
/// through the testable <see cref="CopilotFinalizeDrainCommand.RunAsync"/> seam
/// against a mock server. Mirrors the existing InlineDrain WireMock harness:
/// auth discovery degrades to "None" (no live server at the resolved default),
/// so requests flow without a token.
/// </summary>
public class CopilotFinalizeDrainRunTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Stop();
    }

    const string ShutdownLine =
        """{"type":"session.shutdown","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":52320,"cacheReadTokens":34736,"cacheWriteTokens":17579}}}},"id":"evt-shutdown","timestamp":1234567890}""";

    const string AssistantLine =
        """{"type":"assistant.message","data":{"content":"final turn"},"id":"evt-asst","timestamp":1234567000}""";

    /// <param name="duringTailDrain">Runs inside the tail drain's transcript POST, after the finalizer read the watcher it
    /// asked to stop and before it checks for a running one.</param>
    void StubServer(string sessionId, Action? duringTailDrain = null) {
        // last_line_number: -1 → drain resumes from line 0 (send everything).
        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(duringTailDrain is null
                ? Response.Create().WithStatusCode(200)
                : Response.Create().WithCallback(_ => {
                    duringTailDrain();

                    return new WireMock.ResponseMessage { StatusCode = 200 };
                }));
    }

    CopilotFinalizeDrainCommand Finalizer() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var client   = new FixedCapacitorHttpClient();

        return new CopilotFinalizeDrainCommand(Config.Root, profiles, client, TestWatchers.For(Config.Root, profiles, client), TimeProvider.System);
    }

    static string NewTranscript(TempDir tmp, string contents) =>
        tmp.CreateFile("events.jsonl", contents);

    bool TranscriptPostContainsType(string type) {
        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        foreach (var req in requests) {
            var root = JsonDocument.Parse(req.RequestMessage.Body!).RootElement;

            foreach (var line in root.GetProperty("lines").EnumerateArray()) {
                if (line.GetString() is not { } s) continue;

                try {
                    using var doc = JsonDocument.Parse(s);

                    if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == type) {
                        return true;
                    }
                } catch {
                    // skip non-JSON lines
                }
            }
        }

        return false;
    }

    int TranscriptPostCount() =>
        _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost()).Count;

    [Test]
    public async Task DeliversShutdown_WhenAlreadyPresent() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-present";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n" + ShutdownLine + "\n");

        await Finalizer().RunAsync(
            sessionId, path, endBody: null,
            pollBudget: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100),
            watcherStopGrace: TimeSpan.Zero
        );

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsTrue();
    }

    [Test]
    public async Task DeliversShutdown_WhenWrittenAfterDelay() {
        // Models the real timing: the finalizer starts polling while the hook
        // tail is still being flushed, and session.shutdown only lands later.
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-delayed";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n");

        var runTask = Finalizer().RunAsync(
            sessionId, path, endBody: null,
            pollBudget: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100),
            watcherStopGrace: TimeSpan.Zero
        );

        await Task.Delay(600);
        await File.AppendAllTextAsync(path, ShutdownLine + "\n");

        await runTask;

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsTrue();
    }

    [Test]
    public async Task TimeoutFallback_DrainsExistingTail_WhenShutdownNeverArrives() {
        // Copilot crash / shutdown never written: the budget elapses but the
        // finalizer must still deliver the final assistant turn it can see
        // (the secondary risk — a dropped final turn).
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-timeout";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n");

        await Finalizer().RunAsync(
            sessionId, path, endBody: null,
            pollBudget: TimeSpan.FromMilliseconds(700),
            pollInterval: TimeSpan.FromMilliseconds(200),
            watcherStopGrace: TimeSpan.Zero
        );

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("assistant.message")).IsTrue();
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsFalse();
    }

    const string EndBody = """{"hook_event_name":"sessionEnd","session_id":"s","reason":"user_exit","ended_at":"2026-11-05T03:36:07.0340000+00:00"}""";

    void StubSessionEnd(int status = 200) =>
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status));

    IReadOnlyList<string> SessionEndBodies() =>
        _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .Select(entry => entry.RequestMessage.Body!).ToList();

    HookSpool Spool() => new(Config.Root, TimeProvider.System, _server.Url);

    /// <summary>A real process stands in for the watcher, since the finalizer may stop the one it finds.</summary>
    async Task<DummyProcess> MarkWatcherAlive(string sessionId) {
        var watcher = DummyProcess.StartSleep(60);
        var paths   = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        await File.WriteAllTextAsync(paths.PidFile(sessionId), $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");
        WatcherHeartbeat.Touch(paths.StartedFile(sessionId), DateTimeOffset.UtcNow);
        WatcherHeartbeat.Touch(paths.HeartbeatFile(sessionId), DateTimeOffset.UtcNow);
        return watcher;
    }

    Task<bool> RunQuickly(string sessionId, string path, string? endBody = EndBody) =>
        Finalizer().RunAsync(sessionId, path, endBody, pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(100), watcherStopGrace: TimeSpan.FromMilliseconds(300));

    [Test]
    public async Task EndsTheSession_WithTheHooksBody_AfterTheTail() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-end";
        StubServer(sessionId);
        StubSessionEnd();

        await RunQuickly(sessionId, NewTranscript(tmp, AssistantLine + "\n" + ShutdownLine + "\n"));

        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);

        var posts = _server.LogEntries
            .Select(entry => entry.RequestMessage.Path)
            .Where(path => path.StartsWith("/hooks/", StringComparison.Ordinal))
            .ToList();
        await Assert.That(string.Join(" ", posts)).IsEqualTo("/hooks/transcript /hooks/session-end/copilot");
    }

    // A session with no prompts can have no events.jsonl at all, so shutdown is never seen.
    [Test]
    public async Task EndsTheSession_WhenTheBudgetLapses_WithNoTranscript() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-empty";
        StubServer(sessionId);
        StubSessionEnd();

        await RunQuickly(sessionId, tmp.PathTo("events.jsonl"));

        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
        await Assert.That(TranscriptPostCount()).IsEqualTo(0);
    }

    [Test]
    public async Task EndsNothing_OfADisabledSession() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-disabled";
        StubServer(sessionId);
        StubSessionEnd();
        DisabledSessions.Mark(sessionId, Config.Root);

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(SessionEndBodies()).IsEmpty();
    }

    // The wait for a watcher can run past a person's disable.
    [Test]
    public async Task EndsNothing_WhenDisabledWhileAWatcherStops() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-disabled-stopping";
        StubServer(sessionId);
        StubSessionEnd();
        using var watcher = await MarkWatcherAlive(sessionId);

        var run = Finalizer().RunAsync(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), EndBody,
            pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(50), watcherStopGrace: TimeSpan.FromSeconds(30));

        DisabledSessions.Mark(sessionId, Config.Root);
        File.Delete(WatcherPaths.FromEnvironment(Config.Root).PidFile(sessionId));
        await run;

        await Assert.That(SessionEndBodies()).IsEmpty();
    }

    [Test]
    public async Task EndsTheSession_OnceAWatcherBeingStoppedHasGone() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-stopping";
        StubServer(sessionId);
        StubSessionEnd();
        using var watcher = await MarkWatcherAlive(sessionId);

        var run = Finalizer().RunAsync(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), EndBody,
            pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(50), watcherStopGrace: TimeSpan.FromSeconds(30));

        await Assert.That(SessionEndBodies()).IsEmpty();
        File.Delete(WatcherPaths.FromEnvironment(Config.Root).PidFile(sessionId));
        await run;

        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
    }

    static string WatcherIgnoringStops(TempDir tmp, out Process watcher) {
        var ready = tmp.PathTo("ready");
        watcher = Process.Start(new ProcessStartInfo("sh", ["-c", $"trap '' TERM; touch \"{ready}\"; while :; do sleep 0.1; done"]) { UseShellExecute = false })!;
        for (var i = 0; i < 50 && !File.Exists(ready); i++) Thread.Sleep(100);
        return $"{watcher.Id}\n{PidIdentity.Capture(watcher.Id)}";
    }

    /// <summary>The hook only asks its watcher to stop. The one it asked, still running after the grace, is stopped, so
    /// the end does not overtake lines it has yet to send.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task StopsTheWatcherThatOutlivesTheGrace_AndEndsTheSession() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-handed-watcher";
        StubServer(sessionId);
        StubSessionEnd();

        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        await File.WriteAllTextAsync(paths.PidFile(sessionId), WatcherIgnoringStops(tmp, out var started));
        using var watcher = started;

        try {
            await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

            await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
            await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
        } finally {
            if (!watcher.HasExited) watcher.Kill();
        }
    }

    /// <summary>A watcher started after the finalizer follows a resumed run, which is not this process's to stop. The
    /// end stays spooled, where the resumed run's start supersedes it.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task LeavesAWatcherStartedAfterTheFinalizerRunning() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-resumed-watcher";

        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        await File.WriteAllTextAsync(paths.PidFile(sessionId), "999999\nan-earlier-watcher");
        using var resumed = DummyProcess.StartSleep(30);

        // Swapped in from the drain rather than after starting the run: a drain that beats the swap would find no
        // running watcher and post the end.
        StubServer(sessionId, duringTailDrain: () => {
            File.WriteAllText(paths.PidFile(sessionId), $"{resumed.Pid}\n{PidIdentity.Capture(resumed.Pid)}");
            WatcherHeartbeat.Touch(paths.StartedFile(sessionId), DateTimeOffset.UtcNow);
            WatcherHeartbeat.Touch(paths.HeartbeatFile(sessionId), DateTimeOffset.UtcNow);
        });
        StubSessionEnd();

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(SessionEndBodies()).IsEmpty();
        await Assert.That(resumed.HasExited).IsFalse();
        await Assert.That(Spool().HasBacklog(sessionId)).IsTrue();
    }

    /// <summary>The finalizer ignores termination signals until its end is durable, so a stalled server cannot hold it
    /// past its caps: the end goes to the spool, and the signals are honoured again.</summary>
    [Test]
    public async Task SpoolsTheEnd_WhenTheServerStallsPastThePostCap() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-stalled-end";
        StubServer(sessionId);
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(20)));

        var durableWithBacklog = false;
        var clock = Stopwatch.StartNew();

        await Finalizer().RunAsync(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), EndBody,
            pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(50), watcherStopGrace: TimeSpan.Zero,
            postCap: TimeSpan.FromMilliseconds(500), endDurable: () => durableWithBacklog = Spool().HasBacklog(sessionId));

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(durableWithBacklog).IsTrue();
    }

    [Test]
    public async Task EndsTheSession_WhenTheTailDrainStallsPastItsCap() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-stalled-drain";
        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"last_line_number": -1}"""));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(20)));
        StubSessionEnd();

        var clock = Stopwatch.StartNew();

        await Finalizer().RunAsync(sessionId, NewTranscript(tmp, AssistantLine + "\n" + ShutdownLine + "\n"), EndBody,
            pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(50), watcherStopGrace: TimeSpan.Zero,
            drainCap: TimeSpan.FromMilliseconds(500));

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
    }

    /// <summary>An end the finalizer posts itself settles the span close it owes, as a drain's delivery would.</summary>
    [Test]
    public async Task SettlesTheSpanClose_OfAnEndItPostsItself() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-direct-close";
        StubServer(sessionId);
        StubSessionEnd();
        var end   = EndBody.Replace("\"session_id\":\"s\"", $"\"session_id\":\"{sessionId}\"");
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Capacitor.Cli.Recording.Harness.Copilot.CopilotSpanClose.Record(paths, sessionId, _server.Url, end);

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), endBody: end);

        await Assert.That(Capacitor.Cli.Recording.Harness.Copilot.CopilotSpanClose.Pending(paths, _server.Url)).DoesNotContain(sessionId);
    }

    [Test]
    public async Task SpoolsTheEnd_WhenTheServerCannotTakeIt() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-outage";
        StubServer(sessionId);
        StubSessionEnd(503);

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(Spool().HasBacklog(sessionId)).IsTrue();
    }

    [Test]
    public async Task DoesNotSpoolTheEnd_WhenTheServerRejectsIt() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-rejected";
        StubServer(sessionId);
        StubSessionEnd(400);

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(SessionEndBodies().Count).IsEqualTo(1);
        await Assert.That(Spool().HasBacklog(sessionId)).IsFalse();
    }

    [Test]
    public async Task SendsTheEnd_BehindASpooledStart() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-backlog";
        StubServer(sessionId);
        StubSessionEnd();
        _server.Given(Request.Create().WithPath("/hooks/session-start/copilot").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        Spool().Append(sessionId, "session-start/copilot", "{}");

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        var lifecycle = _server.LogEntries.Select(entry => entry.RequestMessage.Path).Where(path => path.Contains("session-", StringComparison.Ordinal)).ToList();
        await Assert.That(string.Join(" ", lifecycle)).IsEqualTo("/hooks/session-start/copilot /hooks/session-end/copilot");
        await Assert.That(Spool().HasBacklog(sessionId)).IsFalse();
    }

    /// <summary>The hook spools its end before anything else, so the finalizer delivers what it finds there.</summary>
    [Test]
    public async Task SendsTheEndTheHookSpooled_AfterTheTail() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-spooled-end";
        StubServer(sessionId);
        StubSessionEnd();
        Spool().Append(sessionId, "session-end/copilot", EndBody);

        await RunQuickly(sessionId, NewTranscript(tmp, AssistantLine + "\n" + ShutdownLine + "\n"), endBody: null);

        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
        await Assert.That(Spool().HasBacklog(sessionId)).IsFalse();

        var posts = _server.LogEntries.Select(entry => entry.RequestMessage.Path).Where(path => path.StartsWith("/hooks/", StringComparison.Ordinal)).ToList();
        await Assert.That(string.Join(" ", posts)).IsEqualTo("/hooks/transcript /hooks/session-end/copilot");
    }

    /// <summary>A spooled end the server rejects is gone from the spool without having been taken, so nothing that
    /// waits on a taken end, such as closing the workspace span, may follow.</summary>
    [Test]
    public async Task ReportsNoEnd_WhenTheServerRejectsTheSpooledOne() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-spooled-rejected";
        StubServer(sessionId);
        StubSessionEnd(400);
        var end = EndBody.Replace("\"session_id\":\"s\"", $"\"session_id\":\"{sessionId}\"");
        Spool().Append(sessionId, "session-end/copilot", end);

        var taken = await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), endBody: null);

        await Assert.That(SessionEndBodies()).IsEquivalentTo([end]);
        await Assert.That(taken).IsFalse();
    }

    [Test]
    public async Task ReportsTheEnd_WhenTheServerTakesTheSpooledOne() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-spooled-taken";
        StubServer(sessionId);
        StubSessionEnd();
        var end = EndBody.Replace("\"session_id\":\"s\"", $"\"session_id\":\"{sessionId}\"");
        Spool().Append(sessionId, "session-end/copilot", end);

        await Assert.That(await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), endBody: null)).IsTrue();
    }

    /// <summary>A watcher a daemon's claim keeps the kill from reaching is still streaming, so the end waits in the spool
    /// rather than overtaking the lines it has yet to send.</summary>
    [Test]
    public async Task LeavesTheEndSpooled_WhenTheWatcherCannotBeStopped() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-unstoppable";
        StubServer(sessionId);
        StubSessionEnd();

        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        using var watcher = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(paths.PidFile(sessionId), $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");
        WatcherHeartbeat.Touch(paths.StartedFile(sessionId), DateTimeOffset.UtcNow);
        WatcherHeartbeat.Touch(paths.HeartbeatFile(sessionId), DateTimeOffset.UtcNow);
        new RecorderClaims(paths.Directory, TimeProvider.System).TryTake(sessionId, "daemon", isHosted: false);

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(watcher.HasExited).IsFalse();
        await Assert.That(SessionEndBodies()).IsEmpty();
        await Assert.That(Spool().HasBacklog(sessionId)).IsTrue();
    }

    // A hook from an older install starts this command with no end to deliver.
    [Test]
    public async Task SendsNoEnd_WithoutABody() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-no-body";
        StubServer(sessionId);
        StubSessionEnd();

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), endBody: null);

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(SessionEndBodies()).IsEmpty();
    }
}
