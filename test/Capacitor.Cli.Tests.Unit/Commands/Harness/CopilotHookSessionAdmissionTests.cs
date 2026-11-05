using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.PrDetection;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>
/// Which Copilot hook firings belong to a session. Payload shapes are the ones Copilot echoes into
/// <c>events.jsonl</c> as <c>hook.start</c> input.
/// </summary>
public class CopilotHookSessionAdmissionTests : IDisposable {
    const string Parent = "494ed425-f7f6-494a-99cb-4ab2ef7831fe";
    const string Agent  = "56327224-7e62-4f04-ad66-dd8b4168efc8";

    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer     _server  = WireMockServer.Start();
    readonly FakeWatcherSpawner _spawner = new();
    readonly FakeProcessStarter _starter = FakeProcessStarter.Refusing();

    public void Dispose() => _server.Stop();

    CopilotPaths Paths => new(Home, null);

    string StateDir(string dashedId) => Path.Combine(Paths.SessionStateDir, dashedId);

    string Transcript(string dashedId) => Path.Combine(StateDir(dashedId), "events.jsonl");

    string CreateSession(string dashedId) {
        Directory.CreateDirectory(StateDir(dashedId));
        return dashedId;
    }

    Task<int> Fire(string eventName, JsonObject payload) {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var client   = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, client, _starter, WatcherPaths.FromEnvironment(Config.Root), _spawner, TimeProvider.System);

        return new CopilotHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, client, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(new StringReader(payload.ToJsonString()), ["--event", eventName]);
    }

    JsonObject SubagentFiring() => new() {
        ["sessionId"]      = Agent,
        ["transcriptPath"] = Transcript(Parent),
        ["stopReason"]     = "end_turn",
        ["agentId"]        = Agent,
        ["agentType"]      = "task",
        ["parentAgentId"]  = Parent,
        ["timestamp"]      = 1793845392266,
        ["message"]        = "m",
        ["notificationType"] = "agent_idle"
    };

    [Test]
    [Arguments("agentStop")]
    [Arguments("sessionStart")]
    [Arguments("sessionEnd")]
    [Arguments("notification")]
    public async Task a_subagent_firing_creates_nothing_on_any_event(string eventName) {
        CreateSession(Parent);

        await Fire(eventName, SubagentFiring());

        await Assert.That(_spawner.Keys).IsEmpty();
        await Assert.That(_starter.Starts).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task a_firing_reading_another_session_transcript_is_a_subagent_even_without_agent_fields() {
        CreateSession(Parent);

        await Fire("agentStop", new JsonObject { ["sessionId"] = Agent, ["transcriptPath"] = Transcript(Parent) });

        await Assert.That(_spawner.Keys).IsEmpty();
    }

    [Test]
    public async Task a_tool_call_id_is_not_a_session() {
        await Fire("agentStop", new JsonObject { ["sessionId"] = "toolu_01HiQcsri7U45hZNSYY1J4Xv", ["transcriptPath"] = "" });

        await Assert.That(_spawner.Keys).IsEmpty();
    }

    [Test]
    public async Task an_ordinary_session_keeps_its_watcher() {
        CreateSession(Parent);

        await Fire("agentStop", new JsonObject { ["sessionId"] = Parent, ["transcriptPath"] = Transcript(Parent) });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Parent.Replace("-", "")]);
    }

    // sessionStart can run before Copilot has written anything for the session.
    [Test]
    public async Task a_session_with_no_state_dir_and_no_subagent_marker_is_kept() {
        await Fire("agentStop", new JsonObject { ["sessionId"] = Parent });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Parent.Replace("-", "")]);
    }

    [Test]
    public async Task an_empty_transcript_path_is_no_evidence() {
        await Fire("agentStop", new JsonObject { ["sessionId"] = Parent, ["transcriptPath"] = "" });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Parent.Replace("-", "")]);
    }

    // A resumed or forked session has its own state dir, and that outranks every subagent marker.
    [Test]
    public async Task a_session_with_its_own_state_dir_is_kept_whatever_else_the_payload_names() {
        CreateSession(Parent);
        CreateSession(Agent);

        await Fire("agentStop", SubagentFiring());

        await Assert.That(_spawner.Keys).IsEquivalentTo([Agent.Replace("-", "")]);
    }

    [Test]
    public async Task a_session_with_its_own_state_dir_only_in_the_legacy_root_is_kept() {
        Directory.CreateDirectory(Path.Combine(Paths.LegacySessionStateDir, Agent));

        await Fire("agentStop", new JsonObject { ["sessionId"] = Agent, ["transcriptPath"] = Transcript(Parent) });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Agent.Replace("-", "")]);
    }

    // The hook's COPILOT_HOME can disagree with the running Copilot, so the dir beside the reported
    // transcript counts as well as the ones this process resolved.
    [Test]
    public async Task a_session_whose_state_dir_sits_beside_the_reported_transcript_is_kept() {
        var elsewhere = Path.Combine(Home.Path, "relocated", "session-state");
        Directory.CreateDirectory(Path.Combine(elsewhere, Agent));

        await Fire("agentStop", new JsonObject {
            ["sessionId"]      = Agent,
            ["transcriptPath"] = Path.Combine(elsewhere, Parent, "events.jsonl")
        });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Agent.Replace("-", "")]);
    }

    [Test]
    public async Task a_transcript_outside_any_session_dir_is_no_evidence() {
        await Fire("agentStop", new JsonObject {
            ["sessionId"]      = Parent,
            ["transcriptPath"] = Path.Combine(Home.Path, "elsewhere", "events.jsonl")
        });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Parent.Replace("-", "")]);
    }

    [Test]
    public async Task a_new_session_naming_a_parent_before_its_state_dir_exists_is_kept() {
        await Fire("sessionStart", new JsonObject { ["sessionId"] = Agent, ["parentAgentId"] = Parent });

        await Assert.That(_server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task a_later_firing_naming_a_parent_with_no_state_dir_is_a_subagent() {
        await Fire("agentStop", new JsonObject { ["sessionId"] = Agent, ["parentAgentId"] = Parent });

        await Assert.That(_spawner.Keys).IsEmpty();
    }

    [Test]
    public async Task a_transcript_in_a_non_uuid_session_dir_is_another_session() {
        await Fire("agentStop", new JsonObject {
            ["sessionId"]      = Agent,
            ["transcriptPath"] = Path.Combine(StateDir("named-session"), "events.jsonl")
        });

        await Assert.That(_spawner.Keys).IsEmpty();
    }

    // subagentStart and subagentStop carry these fields on the parent's own session id.
    [Test]
    public async Task a_session_naming_itself_as_parent_is_kept() {
        await Fire("agentStop", new JsonObject { ["sessionId"] = Parent, ["agentId"] = Agent, ["parentAgentId"] = Parent });

        await Assert.That(_spawner.Keys).IsEquivalentTo([Parent.Replace("-", "")]);
    }
}
