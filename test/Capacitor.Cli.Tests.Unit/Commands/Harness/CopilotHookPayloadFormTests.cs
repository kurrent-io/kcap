using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>
/// The two payload forms one hook file receives: VS Code's Local harness sends <c>session_id</c> and
/// <c>transcript_path</c> with an ISO timestamp, Copilot CLI and VS Code's Copilot harness send
/// <c>sessionId</c> and a unix-millisecond one.
/// </summary>
public class CopilotHookPayloadFormTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    const string DashedId  = "d72e59eb-1ced-4194-8df4-c3ae346b63d7";
    const string SessionId = "d72e59eb1ced41948df4c3ae346b63d7";

    // Captured from VS Code 1.145's Local harness.
    const string LocalTranscript =
        """
        {"type":"session.start","data":{"sessionId":"d72e59eb-1ced-4194-8df4-c3ae346b63d7","version":1,"producer":"copilot-agent","copilotVersion":"0.73.0","vscodeVersion":"1.145.0","startTime":"2026-11-05T03:39:56.150Z"},"id":"24b4ba4b-0543-4afc-b3c5-aa128944879d","timestamp":"2026-11-05T03:39:56.150Z","parentId":null}
        {"type":"user.message","data":{"content":"Read README.md and tell me the secret word","attachments":[]},"id":"abdf2e64-7dbe-4ac0-8e8b-5974db55fb46","timestamp":"2026-11-05T03:39:56.346Z","parentId":"24b4ba4b-0543-4afc-b3c5-aa128944879d"}
        {"type":"tool.execution_complete","data":{"toolCallId":"call_xjZq6danCFQkK13OzTkaF0yr","success":true},"id":"111e987c-8a0d-4a1c-ae6b-ee9f4a33e66b","timestamp":"2026-11-05T03:39:59.407Z","parentId":"6ec70f45-1824-41b3-a57a-8d18819bc97d"}

        """;

    readonly WireMockServer _server = WireMockServer.Start();
    readonly FakeWatcherSpawner _spawner = new();
    readonly FakeProcessStarter _starter = FakeProcessStarter.Refusing();

    public void Dispose() => _server.Stop();

    CopilotHookCommand Command() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(
            Config.Root, profiles, http, _starter, WatcherPaths.FromEnvironment(Config.Root), _spawner, TimeProvider.System);

        return new CopilotHookCommand(
            Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal,
            http, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory));
    }

    Task<int> Fire(string hookEvent, JsonObject payload) {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"last_line_number": -1}"""));

        return Command().Handle(new StringReader(payload.ToJsonString()), ["hook", "--copilot", "--event", hookEvent]);
    }

    [Test]
    public async Task A_payload_starting_with_a_byte_order_mark_still_starts_the_session() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Command().Handle(new StringReader("\uFEFF" + new JsonObject { ["session_id"] = DashedId }.ToJsonString()), ["hook", "--copilot", "--event", "sessionStart"]);

        await Assert.That(PostedTo("/hooks/session-start/copilot")).IsNotNull();
    }

    JsonObject? PostedTo(string path) =>
        _server.FindLogEntries(Request.Create().WithPath(path).UsingPost()) is [var entry, ..]
            ? JsonNode.Parse(entry.RequestMessage.Body!)!.AsObject()
            : null;

    static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    string SessionStateTranscript() {
        var paths = TestHarnesses.Under(Home).Of<CopilotHarness>().Paths;

        return paths.EventsJsonl(paths.SessionStateDir, DashedId);
    }

    static string LocalTranscriptPath(TempDir dir) {
        var relative = Path.Combine("Application Support", "GitHub.copilot-chat", "transcripts");
        dir.CreateDir(relative);

        return dir.PathTo(Path.Combine(relative, DashedId + ".jsonl"));
    }

    static JsonObject LocalPayload(TempDir dir, string transcriptPath, string hookEventName) => new() {
        ["timestamp"]       = "2026-11-05T03:39:56.152Z",
        ["hook_event_name"] = hookEventName,
        ["session_id"]      = DashedId,
        ["transcript_path"] = transcriptPath,
        ["cwd"]             = dir.Path,
    };

    [Test]
    public async Task A_Local_start_stores_the_session_and_watches_the_transcript_it_names() {
        using var dir = new TempDir();
        var path    = LocalTranscriptPath(dir);
        var payload = LocalPayload(dir, path, "SessionStart");
        payload["source"] = "new";
        payload["model"]  = "auto";

        var exit = await Fire("sessionStart", payload);

        var posted = PostedTo("/hooks/session-start/copilot");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(posted).IsNotNull();
        await Assert.That(posted!["session_id"]!.GetValue<string>()).IsEqualTo(SessionId);
        await Assert.That(posted["source"]!.GetValue<string>()).IsEqualTo("new");
        await Assert.That(Instant(posted["started_at"]!.GetValue<string>())).IsEqualTo(Instant("2026-11-05T03:39:56.152Z"));
        await Assert.That(_spawner.Spawned.Single().TranscriptPath).IsEqualTo(path);
    }

    [Test]
    public async Task A_Copilot_CLI_start_stores_the_session_and_watches_session_state() {
        using var dir = new TempDir();

        var exit = await Fire("sessionStart", new JsonObject {
            ["sessionId"]     = DashedId,
            ["timestamp"]     = 1793849996152L,
            ["cwd"]           = dir.Path,
            ["source"]        = "startup",
            ["initialPrompt"] = "hello",
        });

        var posted = PostedTo("/hooks/session-start/copilot");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(posted!["session_id"]!.GetValue<string>()).IsEqualTo(SessionId);
        await Assert.That(Instant(posted["started_at"]!.GetValue<string>())).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1793849996152L));
        await Assert.That(_spawner.Spawned.Single().TranscriptPath).IsEqualTo(SessionStateTranscript());
    }

    /// <summary>An empty path counts as none, in either spelling: Copilot sends one on some firings.</summary>
    [Test]
    [Arguments("session_id", "transcript_path", "/given/transcripts/t.jsonl", "/given/transcripts/t.jsonl")]
    [Arguments("sessionId", "transcriptPath", "/given/session/events.jsonl", "/given/session/events.jsonl")]
    [Arguments("session_id", "transcript_path", "", null)]
    [Arguments("sessionId", "transcriptPath", "", null)]
    public async Task A_turn_end_keeps_the_watcher_on_the_named_transcript(
            string sessionKey, string pathKey, string given, string? expected) {
        using var dir = new TempDir();

        await Fire("agentStop", new JsonObject { [sessionKey] = DashedId, [pathKey] = given, ["cwd"] = dir.Path });

        await Assert.That(_spawner.Spawned.Single().TranscriptPath).IsEqualTo(expected ?? SessionStateTranscript());
    }

    /// <summary>Local writes no shutdown line, so a detached wait for one would only idle for its whole budget.</summary>
    [Test]
    public async Task A_Local_end_drains_the_named_transcript_and_waits_for_no_shutdown_line() {
        using var dir = new TempDir();
        var path = LocalTranscriptPath(dir);
        await File.WriteAllTextAsync(path, LocalTranscript);

        var exit = await Fire("sessionEnd", LocalPayload(dir, path, "SessionEnd"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_starter.Starts).IsEqualTo(0);

        var drained = PostedTo("/hooks/transcript");
        await Assert.That(drained).IsNotNull();
        await Assert.That(drained!["lines"]!.AsArray().Count).IsEqualTo(3);

        var ended = PostedTo("/hooks/session-end/copilot");
        await Assert.That(ended!["session_id"]!.GetValue<string>()).IsEqualTo(SessionId);
        await Assert.That(Instant(ended["ended_at"]!.GetValue<string>())).IsEqualTo(Instant("2026-11-05T03:39:56.152Z"));
    }

    [Test]
    public async Task A_Copilot_CLI_end_still_leaves_a_drainer_for_the_shutdown_line() {
        using var dir = new TempDir();

        var exit = await Fire("sessionEnd", new JsonObject {
            ["sessionId"] = DashedId, ["timestamp"] = 1793849996152L, ["cwd"] = dir.Path, ["reason"] = "complete",
        });

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_starter.Seen!.ArgumentList).Contains("copilot-finalize");
        await Assert.That(_starter.Seen.ArgumentList).Contains(SessionStateTranscript());
        await Assert.That(PostedTo("/hooks/session-end/copilot")).IsNotNull();
    }

    /// <summary>A subagent firing reuses a tool call id as its session id, in either spelling.</summary>
    [Test]
    [Arguments("session_id")]
    [Arguments("sessionId")]
    public async Task A_non_uuid_session_id_is_not_a_session(string sessionKey) {
        using var dir = new TempDir();

        var exit = await Fire("sessionStart", new JsonObject { [sessionKey] = "toolu_01abc", ["cwd"] = dir.Path });

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(PostedTo("/hooks/session-start/copilot")).IsNull();
        await Assert.That(_spawner.Spawned).IsEmpty();
    }
}
