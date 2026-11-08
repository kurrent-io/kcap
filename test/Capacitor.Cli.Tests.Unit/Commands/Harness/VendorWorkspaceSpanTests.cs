using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>Codex spans each turn from its prompt to its stop; a vendor without turn hooks spans the
/// whole session from its start to its end.</summary>
[ParallelLimiter<SubprocessLimit>]
public class VendorWorkspaceSpanTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [GitRepo] public required GitRepo Repo { get; init; }

    const string Session = "d6c19ec2-5a16-47b2-a311-a59ed6609a25";

    readonly WireMockServer _server = WireMockServer.Start();

    // Never moves, so no snapshot or drain window can run out on a loaded host.
    readonly FakeTimeProvider _time = new();

    public void Dispose() => _server.Stop();

    [Before(Test)]
    public void Answer() {
        _server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(200));
        Repo.CreateFile("README.md", "seed");
        Repo.CommitAll("seed");
    }

    List<WorkspaceChangesRequest> Posted() => _server.LogEntries
        .Where(e => e.RequestMessage.Path == "/hooks/workspace-changes" && e.RequestMessage.Method == "POST")
        .Select(e => JsonSerializer.Deserialize(e.RequestMessage.Body!, WorkspaceJsonContext.Default.WorkspaceChangesRequest)!)
        .ToList();

    Task<int> CodexAsync(string eventName, string extra = "") {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var payload  = $$"""{"hook_event_name":"{{eventName}}","session_id":"{{Session}}","cwd":{{JsonSerializer.Serialize(Repo.Path)}}{{extra}}}""";
        return new CodexHookCommand(Config.Root, profiles, new HookClock(_time), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, http,
                TestWatchers.For(Config.Root, profiles, http), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .Handle(new StringReader(payload), new CodexHookEnvironment(TextWriter.Null, TextWriter.Null, Skip: false, HostedAppServer: false));
    }

    Task<int> CopilotAsync(string eventName) {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, http, FakeProcessStarter.Running(_ => null), WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);
        var payload  = new JsonObject { ["sessionId"] = Session, ["cwd"] = Repo.Path, ["reason"] = "user_exit" };
        return new CopilotHookCommand(Config.Root, profiles, new HookClock(_time), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, http, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(new StringReader(payload.ToJsonString()), ["--event", eventName]);
    }

    List<string> PostedPaths() => _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();

    Task<int> PiAsync(string eventName, string file) {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        return new PiHookCommand(Config.Root, profiles, new HookClock(_time), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, http,
                TestWatchers.For(Config.Root, profiles, http), new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(["--event", eventName, "--file", file, "--cwd", Repo.Path, "--reason", "quit"], TextWriter.Null);
    }

    /// <summary>The session's end goes before its span: a host that kills the hook early must still get it.</summary>
    [Test]
    public async Task Copilot_posts_the_session_end_before_the_span_is_closed() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");
        await CopilotAsync("sessionEnd");

        var posts = PostedPaths();
        await Assert.That(posts).Contains("/hooks/workspace-changes");
        await Assert.That(posts.IndexOf("/hooks/session-end/copilot")).IsLessThan(posts.IndexOf("/hooks/workspace-changes"));
    }

    [Test]
    public async Task Pi_posts_the_session_end_before_the_span_is_closed() {
        using var sessions = new TempDir("pi-sessions");
        var file = sessions.CreateFile($"2026-11-07T10-00-00_{Session}.jsonl", "");
        await PiAsync("session-start", file);
        Repo.CreateFile("made.txt", "x");
        await PiAsync("session-end", file);

        var posts = PostedPaths();
        await Assert.That(posts).Contains("/hooks/workspace-changes");
        await Assert.That(posts.IndexOf("/hooks/session-end/pi")).IsLessThan(posts.IndexOf("/hooks/workspace-changes"));
    }

    /// <summary>A session the user disabled sends nothing more, whichever session's hook drains the spool.</summary>
    [Test]
    public async Task A_disabled_sessions_spooled_span_is_dropped_unsent_by_another_sessions_drain() {
        const string Disabled = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string Live     = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var spool = new WorkspaceSpool(Config.Root, _server.Url!, _time);
        foreach (var session in new[] { Disabled, Live })
            spool.Write(new WorkspaceChangesRequest(session, Guid.NewGuid(), Repo.Path, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                [new WorkspaceChange("x.txt", "edit")], false));
        Directory.CreateDirectory(Config.Root.Path("disabled"));
        File.WriteAllText(Path.Combine(Config.Root.Path("disabled"), Disabled), "");

        await new Capacitor.Cli.Workspace.WorkspaceSpanHook(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient(), _time)
            .DrainAsync(TimeSpan.FromSeconds(30));

        await Assert.That(Posted().Select(p => p.SessionId)).IsEquivalentTo([Live]);
        await Assert.That(spool.HasPending).IsFalse();
    }

    /// <summary>The drain ahead of a hook's dispatch runs before a session end is handed off, so it never
    /// posts a workspace span; those go after the vendor's own work.</summary>
    [Test]
    public async Task The_drain_ahead_of_dispatch_posts_no_workspace_span() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        new WorkspaceSpool(Config.Root, _server.Url!, _time).Write(new WorkspaceChangesRequest(
            "cccccccccccccccccccccccccccccccc", Guid.NewGuid(), Repo.Path, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new WorkspaceChange("x.txt", "edit")], false));

        await new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time)
            .DrainSpoolsCoreAsync(new HookSpool(Config.Root, _time, _server.Url), new TranscriptSpool(Config.Root, _time, _server.Url),
                sessionId: "cccccccccccccccccccccccccccccccc", http.ForHookAsync);

        await Assert.That(Posted()).IsEmpty();
    }

    [Test]
    public async Task The_drain_after_dispatch_posts_workspace_spans_and_withholds_disabled_sessions() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var spool    = new WorkspaceSpool(Config.Root, _server.Url!, _time);
        foreach (var session in new[] { "dddddddddddddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" })
            spool.Write(new WorkspaceChangesRequest(session, Guid.NewGuid(), Repo.Path, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                [new WorkspaceChange("x.txt", "edit")], false));
        Directory.CreateDirectory(Config.Root.Path("disabled"));
        File.WriteAllText(Path.Combine(Config.Root.Path("disabled"), "dddddddddddddddddddddddddddddddd"), "");

        await new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time)
            .DrainWorkspaceAsync(TimeSpan.Zero);

        await Assert.That(Posted().Select(p => p.SessionId)).IsEquivalentTo(["eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"]);
    }

    [Test]
    public async Task Codex_spans_a_turn_from_its_prompt_to_its_stop() {
        await CodexAsync("UserPromptSubmit");
        Repo.CreateFile("codex-turn.txt", "x");
        await CodexAsync("Stop");

        await Assert.That(Posted().SelectMany(p => p.Changes)).IsEquivalentTo([new WorkspaceChange("codex-turn.txt", "create")]);
    }

    [Test]
    public async Task A_codex_subagents_hooks_take_no_span() {
        await CodexAsync("UserPromptSubmit", ""","agent_id":"01a15350-0841-7c70-95e8-30667b7a04b9" """);
        Repo.CreateFile("subagent.txt", "x");
        await CodexAsync("Stop", ""","agent_id":"01a15350-0841-7c70-95e8-30667b7a04b9" """);

        await Assert.That(Posted()).IsEmpty();
    }

    [Test]
    public async Task A_vendor_without_turn_hooks_spans_the_session_from_start_to_end() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("first-turn.txt", "x");
        Repo.CreateFile("second-turn.txt", "x");
        await CopilotAsync("sessionEnd");

        await Assert.That(Posted().SelectMany(p => p.Changes).Select(c => c.Path)).IsEquivalentTo(["first-turn.txt", "second-turn.txt"]);
    }
}
