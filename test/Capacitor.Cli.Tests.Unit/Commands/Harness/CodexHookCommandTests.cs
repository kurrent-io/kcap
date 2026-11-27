using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

// Shares the AuthProviderDiscoveryCache cohort with the rest of the assembly's /auth/config
// stubbers, so this class's own stub cannot race one of theirs.
[NotInParallel("AuthProviderDiscoveryCache")]
[ParallelLimiter<SubprocessLimit>]
public class CodexHookCommandTests : IDisposable {

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly StringWriter   _stdout = new();
    readonly StringWriter   _stderr = new();

    public void Dispose() => _server.Stop();

    string Stdout => _stdout.ToString();

    void StubNoAuthRequired() =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

    Task<int> RunAsync(
            string payload, string? serverUrl = null, HostedAgent? hosted = null, ICapacitorHttpClient? http = null,
            IWatcherSpawner? spawner = null, bool skip = false, bool hostedAppServer = false, bool autoReview = false) {
        var profiles = Resolutions.At(serverUrl ?? _server.Url!, Config.Root);
        var client   = http ?? new FixedCapacitorHttpClient();
        var watchers = spawner is null
            ? TestWatchers.For(Config.Root, profiles, client)
            : TestWatchers.For(Config.Root, profiles, client, spawner);
        var command = new CodexHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
            hosted ?? HostedAgent.Terminal, client, watchers, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None);

        return command.Handle(new StringReader(payload), new CodexHookEnvironment(_stdout, _stderr, skip, hostedAppServer, autoReview));
    }

    [Test]
    public async Task SessionStart_posts_to_session_start_codex_with_normalized_session_id() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = """
                      {
                        "hook_event_name": "SessionStart",
                        "session_id": "019e0322-05fc-7570-be65-75719c3ea861",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp",
                        "model": "gpt-5"
                      }
                      """;

        var exit = await RunAsync(payload, spawner: new FakeWatcherSpawner());

        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonElement.Parse(requests[0].RequestMessage.Body!);
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
        await Assert.That(root.GetProperty("home_dir").GetString()).IsNotNull();

        // The approval policy is frozen at session start, under the key PreToolUse reads it by.
        await Assert.That(new PolicySnapshotStore(Config.Root).TryLoad("019e032205fc7570be6575719c3ea861")).IsNotNull();

        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
    }

    // Codex 'Stop' fires at turn end, not session end: the hook POSTs /hooks/stop for the idle-wait
    // marker but never session-end, which belongs to the watcher's parent-exit fallback.
    [Test]
    public async Task Stop_posts_to_hooks_stop_and_emits_continue_json() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        var exit = await RunAsync(payload);
        await Assert.That(exit).IsEqualTo(0);

        var stopRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
        await Assert.That(stopRequests.Count).IsEqualTo(1);
        var body = JsonElement.Parse(stopRequests[0].RequestMessage.Body!);
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo("abc");
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo("/tmp/rollout.jsonl");
        await Assert.That(body.GetProperty("cwd").GetString()).IsEqualTo("/tmp");
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("Stop");

        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);

        // A single JSON object on stdout, with no watcher chatter.
        var doc = JsonDocument.Parse(Stdout);
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
        await Assert.That(Stdout.Contains("Watcher ")).IsFalse();
        await Assert.That(Stdout.Contains("Spawned")).IsFalse();
    }

    // The POST is best-effort and capped at 2s: a /hooks/stop that stalls 10s still gets
    // {"continue":true} back well under 5s.
    [Test]
    public async Task Stop_still_emits_continue_json_when_server_is_slow() {
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        const string payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await RunAsync(payload);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // The best-effort POST's single deadline covers /auth/config discovery too, not just the POST.
    [Test]
    public async Task Stop_still_emits_continue_json_when_auth_discovery_is_slow() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await RunAsync(payload);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    /// What the hook's poster reports goes to the run's own stderr, which Codex reads as the hook's result.
    [Test]
    public async Task SessionStart_reports_an_unusable_server_on_the_runs_own_stderr() {
        var exit = await RunAsync("""{"hook_event_name":"SessionStart","session_id":"abc","cwd":"/tmp"}""",
            serverUrl: "localhost:5108", spawner: new FakeWatcherSpawner());

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_stderr.ToString()).Contains("session-start/codex spooled, not sent");
        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
    }

    // A scheme-less server URL is refused before any client is built, and the hook still answers.
    [Test]
    public async Task Stop_with_malformed_base_url_still_emits_continue_and_returns_zero() {
        const string payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        var exit = await RunAsync(payload, serverUrl: "localhost:5108");
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
    }

    // With no bridge the request is recorded on /hooks/permission-record and stdout carries no
    // decision, so Codex's own in-CLI prompt asks the user rather than the hook auto-allowing.
    [Test]
    public async Task PermissionRequest_records_event_and_yields_decision_to_codex() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        var exit = await RunAsync(payload);

        await Assert.That(exit).IsEqualTo(0);

        var doc = JsonDocument.Parse(Stdout);
        var hasDecision = doc.RootElement.TryGetProperty("hookSpecificOutput", out var hso)
         && hso.TryGetProperty("decision", out _);
        await Assert.That(hasDecision).IsFalse();

        // Not the long-poll /hooks/permission-request/{vendor} route, which outlives Codex's hook timeout.
        var recorded = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-record").UsingPost());
        await Assert.That(recorded.Count).IsEqualTo(1);

        var wrong = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost());
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    // A slow recording endpoint must not hold the hook near Codex's 30s timeout.
    [Test]
    public async Task PermissionRequest_returns_quickly_when_server_is_slow() {
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        const string payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await RunAsync(payload);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // Auth discovery sits inside the same 2s deadline: without it, a stalled /auth/config would
    // run on HttpClient's 100s default.
    [Test]
    public async Task PermissionRequest_returns_quickly_when_auth_discovery_is_slow() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await RunAsync(payload);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task UserPromptSubmit_PreToolUse_PostToolUse_are_swallowed() {
        foreach (var evt in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse" }) {
            var payload = $$"""
                            {
                              "hook_event_name": "{{evt}}",
                              "session_id": "abc",
                              "transcript_path": "/tmp/r.jsonl",
                              "cwd": "/tmp"
                            }
                            """;

            var exit = await RunAsync(payload);
            await Assert.That(exit).IsEqualTo(0);
        }

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// A deny rule must bind a call Codex runs unasked wherever it runs, the hosted app-server included.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreToolUse_answers_a_deny_rule_in_every_mode(bool hostedAppServer) {
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf *\" }\n    outcome: deny\n");
        const string payload = """{"hook_event_name":"PreToolUse","session_id":"01a15cd7-3e2d-75b2-ad28-c00f7dbcf5d0","tool_name":"Bash","tool_input":{"command":"rm -rf build"},"tool_use_id":"exec-1","cwd":"/tmp"}""";

        var exit = await RunAsync(payload, hostedAppServer: hostedAppServer);

        await Assert.That(exit).IsEqualTo(0);
        var output = JsonElement.Parse(Stdout).GetProperty("hookSpecificOutput");
        await Assert.That(output.GetProperty("permissionDecision").GetString()).IsEqualTo("deny");
    }

    [Test]
    public async Task Unknown_event_returns_zero_and_no_request() {
        var exit = await RunAsync("""{"hook_event_name": "BogusEvent", "session_id": "abc"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_hook_event_name_returns_zero_silently() {
        var exit = await RunAsync("""{"session_id": "abc"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_json_returns_zero_silently() {
        var exit = await RunAsync("{not json");

        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task Hook_event_name_as_number_returns_zero_without_crash() {
        var exit = await RunAsync("""{"hook_event_name": 99, "session_id": "abc", "transcript_path": "/tmp/r.jsonl"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    // A non-string session_id reads as absent, so Stop skips the watcher, and it still never
    // POSTs session-end — the stub is there so a regression shows up in the request log.
    [Test]
    public async Task Stop_with_numeric_session_id_returns_zero_without_crash() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var exit = await RunAsync("""{"hook_event_name": "Stop", "session_id": 12345, "transcript_path": "/tmp/r.jsonl"}""");

        await Assert.That(exit).IsEqualTo(0);

        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);
    }

    // After `kcap disable` the next Stop must not restart the watcher and re-send a session whose
    // data was just deleted server-side.
    [Test]
    public async Task Handle_skips_dispatch_when_session_is_disabled() {
        // Dashless, so the id written to disk is the one the hook looks up after normalizing.
        const string sessionId = "disabledsess123abc";

        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload = $$"""
                        {
                          "hook_event_name": "Stop",
                          "session_id": "{{sessionId}}",
                          "transcript_path": "/tmp/rollout.jsonl",
                          "cwd": "/tmp"
                        }
                        """;

        DisabledSessions.Mark(sessionId, Config.Root);

        var exit = await RunAsync(payload);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
    }

    // An envelope-sourced hosted session's transcript comes from the daemon, so the hook spawns no watcher.

    const string EndingSession  = "019e0322-05fc-7570-be65-75719c3ea861";
    const string EndingDashless = "019e032205fc7570be6575719c3ea861";

    static string SessionEndPayload(string? transcript, string extra = "") =>
        $$"""{"hook_event_name":"SessionEnd","session_id":"{{EndingSession}}","transcript_path":{{JsonSerializer.Serialize(transcript)}},"cwd":"/tmp","reason":"other"{{extra}}}""";

    string Rollout() => Config.CreateFile("rollout.jsonl", "{}\n");

    /// <summary>Codex kills a SessionEnd hook within 3 s, so the end goes to the session's watcher, which drains the
    /// rollout and posts it; nothing is posted from the hook.</summary>
    [Test]
    public async Task SessionEnd_hands_the_end_to_the_sessions_watcher() {
        var spawner = new FakeWatcherSpawner();

        var exit = await RunAsync(SessionEndPayload(Rollout()), spawner: spawner);

        await Assert.That(exit).IsEqualTo(0);
        var paths   = WatcherPaths.FromEnvironment(Config.Root);
        var handed  = HandedSessionEndRequest.TryClaim(paths, EndingDashless, "test");
        await Assert.That(handed).IsNotNull();
        await Assert.That(handed!.Vendor).IsEqualTo("codex");
        await Assert.That(JsonNode.Parse(handed.Payload)!["reason"]!.GetValue<string>()).IsEqualTo("other");
        await Assert.That(spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex")).Count).IsEqualTo(0);
        await Assert.That(Stdout).IsEmpty();
    }

    /// <summary>A hosted agent's end carries its run, so the watcher's fence can place it.</summary>
    [Test]
    public async Task SessionEnd_names_the_hosted_run() {
        var hosted = new HostedAgent("agent-1", false, DaemonBridge.None) { DaemonEpoch = "epoch-1" };

        await RunAsync(SessionEndPayload(Rollout()), hosted: hosted, spawner: new FakeWatcherSpawner());

        var payload = JsonNode.Parse(HandedSessionEndRequest.TryClaim(WatcherPaths.FromEnvironment(Config.Root), EndingDashless, "test")!.Payload)!;
        await Assert.That(payload["agent_host_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(payload["daemon_epoch"]!.GetValue<string>()).IsEqualTo("epoch-1");
    }

    /// <summary>An envelope-sourced hosted session's transcript and end belong to the daemon that hosts it.</summary>
    [Test]
    public async Task SessionEnd_leaves_an_envelope_sourced_hosted_session_to_the_daemon() {
        var spawner = new FakeWatcherSpawner();

        await RunAsync(SessionEndPayload(Rollout()), spawner: spawner, hostedAppServer: true);

        await Assert.That(HandedSessionEndRequest.Exists(WatcherPaths.FromEnvironment(Config.Root), EndingDashless)).IsFalse();
        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
    }

    /// <summary>Codex fires no SessionEnd for a subagent; one that names an agent is ignored all the same.</summary>
    [Test]
    public async Task SessionEnd_of_a_subagent_is_ignored() {
        var spawner = new FakeWatcherSpawner();

        await RunAsync(SessionEndPayload(Rollout(), extra: ""","agent_id":"child-1" """), spawner: spawner);

        await Assert.That(HandedSessionEndRequest.Exists(WatcherPaths.FromEnvironment(Config.Root), EndingDashless)).IsFalse();
        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
    }

    /// <summary>A resumed session carries on past an end handed over before it, so the watcher must drop that end.</summary>
    [Test]
    public async Task A_resumed_SessionStart_marks_the_session_resumed() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await RunAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{EndingSession}}","source":"resume","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: new FakeWatcherSpawner());

        await Assert.That(HandedSessionEndRequest.ResumedSince(WatcherPaths.FromEnvironment(Config.Root), EndingDashless, before)).IsTrue();
    }

    /// <summary>An end handed over before the resume is the earlier process's, and nobody may post it now.</summary>
    [Test]
    public async Task A_resumed_SessionStart_discards_an_end_handed_over_before_it() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        HandedSessionEndRequest.Write(paths, EndingDashless, """{"reason":"other"}""", vendor: "codex");
        File.SetLastWriteTimeUtc(HandedSessionEndRequest.PathFor(paths, EndingDashless, """{"reason":"other"}""", "codex"), DateTime.UtcNow.AddMinutes(-2));

        await RunAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{EndingSession}}","source":"resume","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: new FakeWatcherSpawner());

        await Assert.That(HandedSessionEndRequest.Exists(paths, EndingDashless)).IsFalse();
    }

    /// <summary>A Codex session id is a UUID; a resume naming anything else touches no session's ends, as a glob in
    /// the id would reach other sessions' files.</summary>
    [Test]
    public async Task A_resumed_SessionStart_with_an_id_that_is_no_uuid_touches_no_ends() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        HandedSessionEndRequest.Write(paths, EndingDashless, """{"reason":"other"}""", vendor: "codex");
        File.SetLastWriteTimeUtc(HandedSessionEndRequest.PathFor(paths, EndingDashless, """{"reason":"other"}""", "codex"), DateTime.UtcNow.AddMinutes(-2));

        await RunAsync("""{"hook_event_name":"SessionStart","session_id":"019e*","source":"resume","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: new FakeWatcherSpawner());

        await Assert.That(HandedSessionEndRequest.Exists(paths, EndingDashless)).IsTrue();
    }

    /// <summary>Codex can keep running after an idle detach or an archive, so an end that cannot be handed over is spooled
    /// rather than left to a process exit that may never come.</summary>
    [Test]
    public async Task SessionEnd_spools_the_end_when_it_cannot_be_handed_over() {
        var spawner = new FakeWatcherSpawner();
        var watchersDir = WatcherPaths.FromEnvironment(Config.Root).Directory;
        Directory.CreateDirectory(Path.GetDirectoryName(watchersDir)!);
        File.WriteAllText(watchersDir, "");

        await RunAsync(SessionEndPayload(Rollout()), spawner: spawner);

        var drained = new List<string>();
        await new HookSpool(Config.Root, TimeProvider.System, _server.Url!).DrainAllAsync(EndingDashless,
            (route, _) => { drained.Add(route); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(drained).IsEquivalentTo(["session-end/codex"]);
        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
    }

    /// <summary>The session id names the request's file, so one that is no session id hands nothing over.</summary>
    [Test]
    public async Task SessionEnd_with_a_session_id_that_is_a_path_hands_nothing_over() {
        var spawner = new FakeWatcherSpawner();

        await RunAsync(SessionEndPayload(Rollout()).Replace(EndingSession, "../escaped"), spawner: spawner);

        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
        await Assert.That(Directory.Exists(WatcherPaths.FromEnvironment(Config.Root).Directory)
            && Directory.EnumerateFileSystemEntries(WatcherPaths.FromEnvironment(Config.Root).Directory).Any()).IsFalse();
    }

    /// <summary>A session with no rollout has nothing to drain: its end is spooled for the drain to post.</summary>
    [Test]
    public async Task SessionEnd_without_a_rollout_spools_the_end() {
        var spawner = new FakeWatcherSpawner();

        await RunAsync(SessionEndPayload(transcript: null), spawner: spawner);

        var drained = new List<(string Route, JsonNode Body)>();
        await new HookSpool(Config.Root, TimeProvider.System, _server.Url!).DrainAllAsync(EndingDashless,
            (route, body) => { drained.Add((route, JsonNode.Parse(body)!)); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        var (spooledRoute, spooledBody) = drained.Single();
        await Assert.That(spooledRoute).IsEqualTo("session-end/codex");
        await Assert.That(spooledBody["session_id"]!.GetValue<string>()).IsEqualTo(EndingDashless);
        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Stop_skips_the_watcher_for_an_envelope_sourced_hosted_session() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var spawner = new FakeWatcherSpawner();

        var exit = await RunAsync("""{"hook_event_name":"Stop","session_id":"g1-stop-suppressed","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: spawner, hostedAppServer: true);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
        // Only the watcher is suppressed: the idle-marker stop POST still fires.
        var stopRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
        await Assert.That(stopRequests.Count).IsEqualTo(1);

        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
    }

    /// The positive control for the suppression above: without the marker the watcher spawns.
    [Test]
    public async Task Stop_spawns_the_watcher_without_the_hosted_appserver_marker() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var spawner = new FakeWatcherSpawner();

        var exit = await RunAsync("""{"hook_event_name":"Stop","session_id":"g1-stop-control","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: spawner);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(spawner.Spawned.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SessionStart_skips_the_watcher_for_an_envelope_sourced_hosted_session() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var spawner = new FakeWatcherSpawner();

        var exit = await RunAsync("""{"hook_event_name":"SessionStart","session_id":"g1-start-suppressed","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""",
            spawner: spawner, hostedAppServer: true);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(spawner.Spawned.Count).IsEqualTo(0);
        var startRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(startRequests.Count).IsEqualTo(1);
    }

    // ---- PermissionRequest through the daemon bridge ----

    const string PermissionRequest = """{"hook_event_name":"PermissionRequest","session_id":"s1"}""";

    [Test]
    public async Task PermissionRequest_with_daemon_url_set_posts_to_bridge_and_forwards_response_to_stdout() {
        using var bridge   = WireMockServer.Start();
        const string token = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""")
            );

        var hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse($"http://127.0.0.1:{bridge.Ports[0]}/{token}"));

        var exit = await RunAsync(PermissionRequest, serverUrl: "http://server.example", hosted: hosted);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout).Contains("\"behavior\":\"allow\"");
    }

    /// <summary>In an envelope-sourced hosted session the app-server runtime answers approvals over
    /// Codex's own requestApproval, with a deadline. The hook must step aside — a bridge round-trip
    /// here would answer first through a channel with no deadline. Keyed on the same marker that
    /// suppresses the hook's transcript watcher for such sessions.</summary>
    [Test]
    public async Task PermissionRequest_in_an_envelope_sourced_hosted_session_yields_to_codex_without_posting_to_the_bridge() {
        using var bridge   = WireMockServer.Start();
        const string token = "abc123";
        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));
        var hosted = new HostedAgent(null, IsRendered: true, DaemonBridge.Parse($"http://127.0.0.1:{bridge.Ports[0]}/{token}"));

        var exit = await RunAsync("""{"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"shell","tool_input":{"command":"ls"}}""",
            serverUrl: "http://server.example", hosted: hosted, hostedAppServer: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout.Trim()).IsEqualTo("{}");
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>On auto-review Codex's requestApproval goes to its own reviewer, never the app-server
    /// runtime, so the hook asks the daemon bridge for kcap's policy decision and relays its answer.</summary>
    [Test]
    public async Task PermissionRequest_in_an_auto_review_app_server_session_asks_the_bridge() {
        using var bridge   = WireMockServer.Start();
        const string token = "abc123";
        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var hosted = new HostedAgent(null, IsRendered: true, DaemonBridge.Parse($"http://127.0.0.1:{bridge.Ports[0]}/{token}"));

        var exit = await RunAsync("""{"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"shell","tool_input":{"command":"ls"}}""",
            serverUrl: "http://server.example", hosted: hosted, hostedAppServer: true, autoReview: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout.Trim()).IsEqualTo("{}");
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PermissionRequest_with_daemon_url_emits_deny_and_exits_nonzero_on_500() {
        using var bridge   = WireMockServer.Start();
        const string token = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse($"http://127.0.0.1:{bridge.Ports[0]}/{token}"));

        var exit = await RunAsync(PermissionRequest, serverUrl: "http://server.example", hosted: hosted);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Stdout).Contains("\"behavior\":\"deny\"");
        await Assert.That(_stderr.ToString()).Contains("HTTP 500");
    }

    [Test]
    public async Task PermissionRequest_with_daemon_url_emits_deny_on_connection_refused() {
        // Port 1 refuses at once.
        var hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse("http://127.0.0.1:1/abc"));

        var exit = await RunAsync(PermissionRequest, serverUrl: "http://server.example", hosted: hosted);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Stdout).Contains("\"behavior\":\"deny\"");
    }

    [Test]
    public async Task PermissionRequest_with_non_loopback_daemon_url_emits_deny_without_posting() {
        using var bridge = WireMockServer.Start();
        var       hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse($"http://example.com:{bridge.Ports[0]}/abc"));

        var exit = await RunAsync(PermissionRequest, serverUrl: "http://server.example", hosted: hosted);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Stdout).Contains("\"behavior\":\"deny\"");
        await Assert.That(_stderr.ToString()).Contains(HostedAgent.BridgeUrlVar);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A blank variable names no bridge, so the request belongs on the stub path where Codex asks
    /// the user itself. Reading it as an address instead refuses the address and then, because this
    /// hook fails closed, the tool call with it — every one, for the whole session.
    /// </summary>
    [Test]
    public async Task PermissionRequest_with_a_blank_daemon_url_yields_to_codex_rather_than_denying() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse(""));

        var exit = await RunAsync("""{"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"shell","tool_input":{"command":"ls"}}""",
            hosted: hosted);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout.Trim()).IsEqualTo("{}");
    }

    [Test]
    public async Task PermissionRequest_with_https_daemon_url_emits_deny_without_posting() {
        using var bridge = WireMockServer.Start();
        var       hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse($"https://127.0.0.1:{bridge.Ports[0]}/abc"));

        var exit = await RunAsync(PermissionRequest, serverUrl: "http://server.example", hosted: hosted);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Stdout).Contains("\"behavior\":\"deny\"");
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PermissionRequest_with_daemon_url_does_not_double_post_to_server_hooks_endpoint() {
        using var bridge   = WireMockServer.Start();
        using var server   = WireMockServer.Start();
        const string token = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""")
            );

        server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var hosted = new HostedAgent(null, IsRendered: false, DaemonBridge.Parse($"http://127.0.0.1:{bridge.Ports[0]}/{token}"));

        await RunAsync(PermissionRequest, serverUrl: $"http://127.0.0.1:{server.Ports[0]}", hosted: hosted);

        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(1);
    }

    // KCAP_SKIP marks a kcap-launched headless Codex flow: every side effect is suppressed, but the
    // hook still owes Codex its stdout, since empty output on SessionStart or Stop is a protocol error.

    [Test]
    [Arguments("SessionStart")]
    [Arguments("Stop")]
    public async Task KcapSkip_emits_continue_json_and_skips_server(string eventName) {
        var exit = await RunAsync($$"""{"hook_event_name":"{{eventName}}","session_id":"abc","cwd":"/tmp","transcript_path":"/tmp/r.jsonl"}""",
            skip: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(JsonElement.Parse(Stdout).GetProperty("continue").GetBoolean()).IsTrue();
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// An empty object is no decision, so Codex falls back to its own approval prompt.
    [Test]
    public async Task KcapSkip_PermissionRequest_emits_empty_object_and_skips_server() {
        var exit = await RunAsync("""{"hook_event_name":"PermissionRequest","session_id":"abc","tool_name":"shell"}""", skip: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout).IsEqualTo("{}");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// PreToolUse has no stdout contract, so a skipped one writes nothing, not even the deny a rule
    /// would otherwise answer with.
    [Test]
    public async Task KcapSkip_PreToolUse_is_silent_and_skips_server() {
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf *\" }\n    outcome: deny\n");

        var exit = await RunAsync("""{"hook_event_name":"PreToolUse","session_id":"01a15cd7-3e2d-75b2-ad28-c00f7dbcf5d0","tool_name":"Bash","tool_input":{"command":"rm -rf build"},"cwd":"/tmp"}""",
            skip: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Stdout).IsEqualTo(string.Empty);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bridge_payload_adds_agent_id_beside_the_hooks_own_cwd() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","cwd":"/repo","tool_name":"shell","tool_input":{"command":"ls"}}""")!;
        var bridge = CodexHookCommand.BuildBridgePayload(node, "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(node["agent_id"]).IsNull().Because("the hook's own node is not mutated");
    }

    /// A subagent's permission hook carries the subagent in its own <c>agent_id</c>, the field the
    /// bridge reads as the hosted agent. It moves to <c>subagent_id</c>, dashless as the tool-settled
    /// relay sends it, so the main agent's turn end leaves the subagent's prompt alone.
    [Test]
    [Arguments("agent-1")]
    [Arguments(null)]
    public async Task Bridge_payload_carries_a_subagents_agent_id_as_its_subagent_id(string? hostedId) {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","agent_id":"01a15350-0841-7c70-95e8-30667b7a04b9","agent_type":"default","tool_name":"Bash","tool_input":{"command":"ls"}}""")!;
        var bridge = CodexHookCommand.BuildBridgePayload(node, hostedId);
        await Assert.That(bridge["subagent_id"]!.GetValue<string>()).IsEqualTo("01a1535008417c7095e830667b7a04b9");
        await Assert.That(bridge["agent_id"]?.GetValue<string>()).IsEqualTo(hostedId);
    }

    sealed class Accepting : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// The bridge post carries the raw tool input to an address the daemon minted, so it must draw the
    /// lane that ignores an ambient proxy — the agent's own environment supplies one often enough, and
    /// a proxied hop would take the payload off the machine.
    /// </summary>
    [Test]
    public async Task The_permission_request_bridge_draws_the_loopback_lane() {
        var       hosted  = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback("http://127.0.0.1:51234/bridge"));
        using var handler = new Accepting();
        var       http    = new RecordingCapacitorHttpClient(handler);

        const string payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        var exit = await RunAsync(payload, hosted: hosted, http: http);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "Loopback" });
    }
}
