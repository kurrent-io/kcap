using System.Text.Json;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
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

    void StubServer(string sessionId) {
        // last_line_number: -1 → drain resumes from line 0 (send everything).
        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
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

    async Task MarkWatcherAlive(string sessionId) {
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        await File.WriteAllTextAsync(paths.PidFile(sessionId), $"{Environment.ProcessId}\n{PidIdentity.Capture(Environment.ProcessId)}");
        WatcherHeartbeat.Touch(paths.StartedFile(sessionId), DateTimeOffset.UtcNow);
        WatcherHeartbeat.Touch(paths.HeartbeatFile(sessionId), DateTimeOffset.UtcNow);
    }

    Task RunQuickly(string sessionId, string path, string? endBody = EndBody) =>
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

    // A live watcher owns the end: a resumed run must stay open, and a watcher the hook never stopped
    // sends its own parent-exit end. A terminal shutdown proves nothing here, since a resumed run may not
    // have written yet.
    [Test]
    [Arguments("no-shutdown")]
    [Arguments("terminal-shutdown")]
    [Arguments("earlier-runs-shutdown")]
    public async Task LeavesTheEnd_ToALiveWatcher(string transcript) {
        using var tmp = new TempDir();
        var sessionId = $"test-finalize-watcher-{transcript}";
        StubServer(sessionId);
        StubSessionEnd();
        await MarkWatcherAlive(sessionId);

        var lines = transcript switch {
            "no-shutdown"       => AssistantLine + "\n",
            "terminal-shutdown" => ShutdownLine + "\n",
            _                   => ShutdownLine + "\n" + AssistantLine + "\n"
        };

        await RunQuickly(sessionId, NewTranscript(tmp, lines));

        await Assert.That(SessionEndBodies()).IsEmpty();
    }

    // A watcher the hook is stopping sends no end of its own.
    [Test]
    public async Task EndsTheSession_OnceAWatcherBeingStoppedHasGone() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-stopping";
        StubServer(sessionId);
        StubSessionEnd();
        await MarkWatcherAlive(sessionId);

        var run = Finalizer().RunAsync(sessionId, NewTranscript(tmp, ShutdownLine + "\n"), EndBody,
            pollBudget: TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(50), watcherStopGrace: TimeSpan.FromSeconds(30));

        await Assert.That(SessionEndBodies()).IsEmpty();
        File.Delete(WatcherPaths.FromEnvironment(Config.Root).PidFile(sessionId));
        await run;

        await Assert.That(SessionEndBodies()).IsEquivalentTo([EndBody]);
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
    public async Task QueuesTheEnd_BehindASpooledStart() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-backlog";
        StubServer(sessionId);
        StubSessionEnd();
        Spool().Append(sessionId, "session-start/copilot", "{}");

        await RunQuickly(sessionId, NewTranscript(tmp, ShutdownLine + "\n"));

        await Assert.That(SessionEndBodies()).IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Spool().DirectoryPath, $"{sessionId}.jsonl"))).Contains("session-end/copilot");
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
