using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Recording.Harness.Copilot;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using TUnit.Core.Enums;
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

    Task<int> CopilotAsync(string eventName, string? transcriptPath = null) {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, http, FakeProcessStarter.Running(_ => null), WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);
        var payload  = new JsonObject { ["sessionId"] = Session, ["cwd"] = Repo.Path, ["reason"] = "user_exit" };
        if (transcriptPath is not null) payload["transcriptPath"] = transcriptPath;
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

    /// <summary>A Copilot hook that handed its end to the finalizer leaves the span to it as well.</summary>
    [Test]
    public async Task The_Copilot_finalizer_closes_the_span_after_the_end_it_posts() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var profiles   = Resolutions.At(_server.Url!, Config.Root);
        var http       = new FixedCapacitorHttpClient();
        var transcript = Home.CreateFile("events.jsonl", """{"type":"session.shutdown","data":{},"id":"evt-shutdown","timestamp":1}""" + "\n");
        var end        = new JsonObject { ["session_id"] = Session.Replace("-", ""), ["reason"] = "user_exit" }.ToJsonString();

        await new CopilotFinalizeDrainCommand(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time)
            .RunAsync(Session.Replace("-", ""), transcript, end, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        var posts = PostedPaths();
        await Assert.That(posts).Contains("/hooks/workspace-changes");
        await Assert.That(posts.IndexOf("/hooks/session-end/copilot")).IsLessThan(posts.IndexOf("/hooks/workspace-changes"));
    }

    /// <summary>The drain ahead of every hook spends nothing on spans: under Copilot's shared shutdown budget, closing
    /// another chat's span there would starve this hook's own end.</summary>
    [Test]
    public async Task A_drained_Copilot_end_does_no_span_work() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var canonical = Session.Replace("-", "");
        var profiles  = Resolutions.At(_server.Url!, Config.Root);
        var http      = new FixedCapacitorHttpClient();
        var spool     = new HookSpool(Config.Root, _time, _server.Url);
        var end       = new JsonObject { ["session_id"] = canonical, ["reason"] = "parent_exited" }.ToJsonString();
        CopilotSpanClose.Record(WatcherPaths.FromEnvironment(Config.Root), canonical, _server.Url, end);
        spool.Append(canonical, "session-end/copilot", end);

        await new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time).DrainSpoolsNowAsync(spool, canonical);

        await Assert.That(PostedPaths()).Contains("/hooks/session-end/copilot");
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
        await Assert.That(CopilotSpanClose.IsDelivered(WatcherPaths.FromEnvironment(Config.Root), canonical)).IsTrue();
    }

    /// <summary>A delivery the drain could not mark would lose the span close, so the end stays spooled for a retry,
    /// which the server takes as the same end.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_Copilot_end_stays_spooled_when_its_delivery_cannot_be_marked() {
        var canonical = Session.Replace("-", "");
        var paths     = WatcherPaths.FromEnvironment(Config.Root);
        var profiles  = Resolutions.At(_server.Url!, Config.Root);
        var http      = new FixedCapacitorHttpClient();
        var spool     = new HookSpool(Config.Root, _time, _server.Url);
        var end       = new JsonObject { ["session_id"] = canonical, ["reason"] = "user_exit" }.ToJsonString();
        CopilotSpanClose.Record(paths, canonical, _server.Url, end);
        spool.Append(canonical, "session-end/copilot", end);
        File.SetUnixFileMode(paths.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            await new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time).DrainSpoolsNowAsync(spool, canonical);

            await Assert.That(spool.HasBacklog(canonical)).IsTrue();
        } finally {
            File.SetUnixFileMode(paths.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A watcher still stopping may yet spool the tail its end must follow, so drains hold that end until the
    /// watcher is gone.</summary>
    [Test]
    public async Task A_drain_holds_a_Copilot_end_while_its_watcher_runs() {
        var canonical = Session.Replace("-", "");
        var paths     = WatcherPaths.FromEnvironment(Config.Root);
        var profiles  = Resolutions.At(_server.Url!, Config.Root);
        var http      = new FixedCapacitorHttpClient();
        var spool     = new HookSpool(Config.Root, _time, _server.Url);
        spool.Append(canonical, "session-end/copilot", new JsonObject { ["session_id"] = canonical, ["reason"] = "user_exit", ["ended_at"] = _time.GetUtcNow().ToString("O") }.ToJsonString());

        Directory.CreateDirectory(paths.Directory);
        var watcher = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(paths.PidFile(canonical), $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");
        var poster = new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time);

        await poster.DrainSpoolsNowAsync(spool, canonical);
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/session-end/copilot");

        watcher.Kill();
        watcher.WaitForExit(TimeSpan.FromSeconds(5));
        watcher.Dispose();

        await poster.DrainSpoolsNowAsync(spool, canonical);
        await Assert.That(PostedPaths()).Contains("/hooks/session-end/copilot");
    }

    /// <summary>A close owed by an earlier run's end closes only that run's spans: one a new run opened while the close was
    /// on its way is left open.</summary>
    [Test]
    public async Task An_owed_span_close_leaves_a_later_runs_span_open() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var canonical = Session.Replace("-", "");
        var paths     = WatcherPaths.FromEnvironment(Config.Root);
        var end       = new JsonObject { ["session_id"] = canonical, ["reason"] = "user_exit" }.ToJsonString();
        CopilotSpanClose.Record(paths, canonical, _server.Url, end, new FakeTimeProvider(_time.GetUtcNow() - TimeSpan.FromMinutes(1)));
        CopilotSpanClose.NoteOutcome(paths, "session-end/copilot", end, DrainOutcome.Delivered);

        var profiles   = Resolutions.At(_server.Url!, Config.Root);
        var http       = new FixedCapacitorHttpClient();
        var transcript = Home.CreateFile("other.jsonl", """{"type":"session.shutdown","data":{},"id":"evt-shutdown","timestamp":1}""" + "\n");

        await new CopilotFinalizeDrainCommand(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time)
            .RunAsync("another-session", transcript, endBody: null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
    }

    /// <summary>The finalizer closes only the spans of the run whose end it carries: one a new run opened while it waited
    /// is that run's.</summary>
    [Test]
    public async Task The_Copilot_finalizer_leaves_a_span_a_new_run_opened_while_it_waited() {
        var finalizerTime = new FakeTimeProvider(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(1));
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var canonical  = Session.Replace("-", "");
        var profiles   = Resolutions.At(_server.Url!, Config.Root);
        var http       = new FixedCapacitorHttpClient();
        var transcript = Home.CreateFile("events.jsonl", """{"type":"session.shutdown","data":{},"id":"evt-shutdown","timestamp":1}""" + "\n");
        var end        = new JsonObject { ["session_id"] = canonical, ["reason"] = "user_exit" }.ToJsonString();

        await new CopilotFinalizeDrainCommand(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), finalizerTime)
            .RunAsync(canonical, transcript, end, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        await Assert.That(PostedPaths()).Contains("/hooks/session-end/copilot");
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
    }

    /// <summary>A watcher's own end closes only the spans of the run it watched: one a replacement run opened after the stop
    /// is that run's.</summary>
    [Test]
    public async Task A_Copilot_watchers_end_leaves_a_span_a_replacement_run_opened() {
        var stoppedAt = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMinutes(1));
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var watch    = new WatchCommand(Config.Root, profiles, TestHarnesses.Under(Home), http, new FixedCredentialSource(), TestWatchers.For(Config.Root, profiles, http),
            new GitProviderRouter(), _time, TestAccounts.None, Home, new(_server.Url, Config.Root, profiles));

        await watch.PostCopilotEndAsync(Session.Replace("-", ""), Home.CreateFile("events.jsonl", ""), Repo.Path, repository: null, "parent_exited", new WorkspaceRunCutoff(null, stoppedAt));

        await Assert.That(PostedPaths()).Contains("/hooks/session-end/copilot");
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
    }

    const string RunStart  = """{"type":"session.start","data":{},"id":"evt-start","timestamp":1}""";
    const string RunResume = """{"type":"session.resume","data":{},"id":"evt-resume","timestamp":2}""";

    WatchCommand Watch() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        return new WatchCommand(Config.Root, profiles, TestHarnesses.Under(Home), http, new FixedCredentialSource(), TestWatchers.For(Config.Root, profiles, http),
            new GitProviderRouter(), _time, TestAccounts.None, Home, new(_server.Url, Config.Root, profiles));
    }

    /// <summary>VS Code can quit while a run's sessionStart hook is still on its way, so the stop comes before that run's
    /// span opens. The span still carries the run it opened in, and the run's end closes it.</summary>
    [Test]
    public async Task A_Copilot_end_closes_its_runs_span_opened_after_the_stop() {
        var transcript = Home.CreateFile("run/events.jsonl", RunStart + "\n");
        var stoppedAt  = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMinutes(1));
        await CopilotAsync("sessionStart", transcript);
        Repo.CreateFile("made.txt", "x");

        await Watch().PostCopilotEndAsync(Session.Replace("-", ""), transcript, Repo.Path, repository: null, "parent_exited",
            new WorkspaceRunCutoff(CopilotRunBoundary.Latest(transcript), stoppedAt));

        await Assert.That(PostedPaths()).Contains("/hooks/workspace-changes");
    }

    /// <summary>A span a resumed run opened has the resume's later boundary, so the earlier run's end leaves it.</summary>
    [Test]
    public async Task A_Copilot_end_leaves_the_span_of_a_run_resumed_after_it() {
        var transcript = Home.CreateFile("run/events.jsonl", RunStart + "\n");
        var ended      = new WorkspaceRunCutoff(CopilotRunBoundary.Latest(transcript), _time.GetUtcNow() + TimeSpan.FromHours(1));
        await File.AppendAllTextAsync(transcript, RunResume + "\n");
        await CopilotAsync("sessionStart", transcript);
        Repo.CreateFile("made.txt", "x");

        await Watch().PostCopilotEndAsync(Session.Replace("-", ""), transcript, Repo.Path, repository: null, "parent_exited", ended);

        await Assert.That(PostedPaths()).Contains("/hooks/session-end/copilot");
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
    }

    /// <summary>Copilot fires sessionStart once per run, so a baseline an earlier run's close could not end is replaced at
    /// the start rather than carried into the new run.</summary>
    [Test]
    public async Task A_Copilot_sessionStart_starts_a_fresh_baseline_over_a_surviving_one() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("earlier.txt", "x");
        _time.Advance(TimeSpan.FromMinutes(1));

        var spool   = new WorkspaceSpool(Config.Root, _server.Url, _time);
        Directory.CreateDirectory(spool.DirectoryPath);
        var blocker = Path.Combine(spool.DirectoryPath, Session.Replace("-", ""));
        File.WriteAllText(blocker, "");
        await CopilotAsync("sessionStart");
        File.Delete(blocker);

        var baselines = Directory.EnumerateFiles(Config.Root.Path(WorkspaceSpanStore.DirectoryName), "*.json", SearchOption.AllDirectories)
            .Select(file => JsonSerializer.Deserialize(File.ReadAllText(file), WorkspaceJsonContext.Default.WorkspaceSpan)!)
            .ToList();

        await Assert.That(baselines).HasSingleItem();
        await Assert.That(baselines[0].OpenedAt).IsEqualTo(_time.GetUtcNow());
    }

    /// <summary>The finalizer runs detached, so it pays what drains owed.</summary>
    [Test]
    public async Task The_Copilot_finalizer_closes_an_owed_span() {
        await CopilotAsync("sessionStart");
        Repo.CreateFile("made.txt", "x");

        var canonical = Session.Replace("-", "");
        var paths     = WatcherPaths.FromEnvironment(Config.Root);
        var end       = new JsonObject { ["session_id"] = canonical, ["reason"] = "parent_exited" }.ToJsonString();
        CopilotSpanClose.Record(paths, canonical, _server.Url, end);
        CopilotSpanClose.NoteOutcome(paths, "session-end/copilot", end, DrainOutcome.Delivered);

        var profiles   = Resolutions.At(_server.Url!, Config.Root);
        var http       = new FixedCapacitorHttpClient();
        var transcript = Home.CreateFile("other.jsonl", """{"type":"session.shutdown","data":{},"id":"evt-shutdown","timestamp":1}""" + "\n");

        await new CopilotFinalizeDrainCommand(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time)
            .RunAsync("another-session", transcript, endBody: null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        await Assert.That(PostedPaths()).Contains("/hooks/workspace-changes");
        await Assert.That(CopilotSpanClose.Pending(paths, _server.Url)).DoesNotContain(canonical);
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

    /// <summary>Pi's end post has no spool, so an end the server did not take leaves the span open.</summary>
    [Test]
    public async Task Pi_leaves_the_span_open_when_its_end_is_not_taken() {
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400));
        _server.Given(Request.Create().UsingAnyMethod()).AtPriority(10).RespondWith(Response.Create().WithStatusCode(200));
        using var sessions = new TempDir("pi-sessions");
        var file = sessions.CreateFile($"2026-11-07T10-00-00_{Session}.jsonl", "");
        await PiAsync("session-start", file);
        Repo.CreateFile("made.txt", "x");
        await PiAsync("session-end", file);

        await Assert.That(_server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-end/pi").ResponseMessage.StatusCode).IsEqualTo(400);
        await Assert.That(PostedPaths()).DoesNotContain("/hooks/workspace-changes");
        await Assert.That(new WorkspaceSpool(Config.Root, _server.Url!, _time).HasPending).IsFalse();
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

    /// <summary>Codex reads a hook's answer when its process exits, so a workspace drain against a server that
    /// never answers must not hold the process past the handshake.</summary>
    [Test]
    [Arguments("SessionStart")]
    [Arguments("Stop")]
    public async Task A_codex_hook_never_waits_on_a_hung_workspace_drain(string eventName) {
        _server.ResetMappings();
        _server.Given(Request.Create().WithPath("/hooks/workspace-changes").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(20)));
        _server.Given(Request.Create().UsingAnyMethod()).AtPriority(10).RespondWith(Response.Create().WithStatusCode(200));
        new WorkspaceSpool(Config.Root, _server.Url!, _time).Write(new WorkspaceChangesRequest(
            "ffffffffffffffffffffffffffffffff", Guid.NewGuid(), Repo.Path, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new WorkspaceChange("x.txt", "edit")], false));
        var started = TimeProvider.System.GetTimestamp();

        await CodexAsync(eventName);

        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
    }

    WatchCommand Watcher() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        return new WatchCommand(Config.Root, profiles, TestHarnesses.Under(Home), http, new FixedCredentialSource(),
            TestWatchers.For(Config.Root, profiles, http), new GitProviderRouter(), _time, TestAccounts.None, Home,
            new CapacitorServer(_server.Url!, Config.Root, profiles));
    }

    /// <summary>A Codex session that dies mid-turn runs no other hook; its watcher posts the end and then
    /// delivers the span itself.</summary>
    [Test]
    public async Task The_watcher_delivers_a_codex_span_when_no_other_hook_runs() {
        await CodexAsync("UserPromptSubmit");
        Repo.CreateFile("last-turn.txt", "x");

        await Watcher().PostSessionEndOnParentExitAsync(Session.Replace("-", ""), Repo.PathTo("rollout.jsonl"), Repo.Path, "codex", repository: null);

        await Assert.That(Posted().SelectMany(p => p.Changes).Select(c => c.Path)).IsEquivalentTo(["last-turn.txt"]);
    }

    /// <summary>A drain still in flight when its hook process is killed must not hold the next one back. The
    /// throttle stamp is written only once a drain returns; the lease a killed drain held is released by the
    /// OS, which <c>SpoolDrainLeaseTests</c> pins.</summary>
    [Test]
    public async Task A_drain_still_in_flight_leaves_no_throttle_stamp() {
        _server.ResetMappings();
        // Counted on arrival: the server logs a request only once it has answered.
        var arrived = 0;
        using var release = new ManualResetEventSlim();
        _server.Given(Request.Create().WithPath("/hooks/workspace-changes").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithCallback(_ => {
                Interlocked.Increment(ref arrived);
                release.Wait(TimeSpan.FromSeconds(20));
                return new WireMock.ResponseMessage { StatusCode = 200 };
            }));
        _server.Given(Request.Create().UsingAnyMethod()).AtPriority(10).RespondWith(Response.Create().WithStatusCode(200));
        var spool = new WorkspaceSpool(Config.Root, _server.Url!, _time);
        spool.Write(new WorkspaceChangesRequest(
            "ffffffffffffffffffffffffffffffff", Guid.NewGuid(), Repo.Path, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new WorkspaceChange("x.txt", "edit")], false));
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var stamp    = Path.Combine(spool.DirectoryPath, ".last-drain");

        var drain = new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), _time).DrainWorkspaceAsync(TimeSpan.Zero);
        for (var i = 0; i < 200 && Volatile.Read(ref arrived) == 0; i++) await Task.Delay(50);
        await Assert.That(Volatile.Read(ref arrived)).IsEqualTo(1);

        await Assert.That(File.Exists(stamp)).IsFalse();
        release.Set();
        await drain;
        await Assert.That(File.Exists(stamp)).IsTrue();
    }

    [Test]
    public async Task Codex_spans_a_turn_from_its_prompt_to_its_stop() {
        await CodexAsync("UserPromptSubmit");
        Repo.CreateFile("codex-turn.txt", "x");
        await CodexAsync("Stop");

        // Codex reads a hook's answer when its process exits, so the span is left for the watcher to post.
        var spooled = Directory.EnumerateFiles(new WorkspaceSpool(Config.Root, _server.Url!, _time).DirectoryPath, "*.json", SearchOption.AllDirectories)
            .Select(f => System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(f), WorkspaceJsonContext.Default.WorkspaceChangesRequest)!)
            .SelectMany(r => r.Changes);
        await Assert.That(spooled).IsEquivalentTo([new WorkspaceChange("codex-turn.txt", "create")]);
        await Assert.That(Posted()).IsEmpty();
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
