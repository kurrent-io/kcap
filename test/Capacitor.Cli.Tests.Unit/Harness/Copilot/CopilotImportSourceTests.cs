using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Tests.Unit.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Discovery-phase tests for <see cref="CopilotImportSource"/> against fake
/// <c>session-state/</c> trees: scaffolding-dir skipping, current-root
/// precedence over the legacy root, workspace.yaml metadata extraction, and
/// the --session / --cwd / --since filters.
/// </summary>
public class CopilotImportSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    /// <summary>Copilot's layout rooted at a throwaway dir, so discovery walks the real
    /// <c>session-state/</c> and <c>history-session-state/</c> names rather than dirs Copilot
    /// would never use.</summary>
    static CopilotPaths Layout(TempDir tmp) => new(new UserHome(tmp.Path), copilotHome: tmp.Path);

    const string Sid1 = "1053bee4-574f-40e3-84ca-463bd7a82dc2";
    const string Sid2 = "4ae28a73-dd66-46ac-81d2-be94b5e87079";

    [Test]
    public async Task discovery_skips_scaffolding_dirs_without_events_jsonl() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/a");
        // Failed-startup scaffolding: workspace.yaml but no events.jsonl.
        Directory.CreateDirectory(Path.Combine(paths.SessionStateDir, Sid2));
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionStateDir, Sid2, "workspace.yaml"), $"id: {Sid2}\ncwd: /work/b\n");

        var source   = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(sessions[0].Vendor).IsEqualTo(HarnessId.Copilot);
    }

    [Test]
    public async Task discovery_reads_workspace_yaml_metadata() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(
            paths.SessionStateDir, Sid1,
            cwd: "/work/a",
            name: "Create a file hello.txt containing 'hello world'",
            createdAt: "2026-06-10T20:23:25.556Z");

        var source   = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z", CultureInfo.InvariantCulture));
        await Assert.That(sessions[0].SourceMeta["Name"]).IsEqualTo("Create a file hello.txt containing 'hello world'");
    }

    [Test]
    public async Task discovery_prefers_current_root_over_legacy_for_same_session() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);

        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/current");
        WriteSession(paths.LegacySessionStateDir, Sid1, cwd: "/work/legacy");
        WriteSession(paths.LegacySessionStateDir, Sid2, cwd: "/work/legacy-only");

        var source   = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(2);

        var migrated = sessions.Single(s => s.SessionId == Sid1.Replace("-", ""));
        await Assert.That(migrated.Cwd).IsEqualTo("/work/current");
        await Assert.That(sessions.Any(s => s.SessionId == Sid2.Replace("-", ""))).IsTrue();
    }

    [Test]
    public async Task session_filter_matches_dashless_and_dashed_input() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/a");
        WriteSession(paths.SessionStateDir, Sid2, cwd: "/work/b");

        var source = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);

        var byDashed = await source.DiscoverAsync(new DiscoveryFilters(null, Sid1, null, 0), CancellationToken.None);
        await Assert.That(byDashed.Count).IsEqualTo(1);
        await Assert.That(byDashed[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));

        var byDashless = await source.DiscoverAsync(
            new DiscoveryFilters(null, Sid1.Replace("-", ""), null, 0), CancellationToken.None);
        await Assert.That(byDashless.Count).IsEqualTo(1);
    }

    [Test]
    public async Task cwd_filter_excludes_other_workspaces_and_sessions_without_cwd() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/a");
        WriteSession(paths.SessionStateDir, Sid2, cwd: null);   // no workspace.yaml cwd

        var source  = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var matched = await source.DiscoverAsync(new DiscoveryFilters("/work/a", null, null, 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
    }

    [Test]
    public async Task since_filter_gates_on_session_start() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/a", createdAt: "2026-06-01T10:00:00Z");
        WriteSession(paths.SessionStateDir, Sid2, cwd: "/work/b", createdAt: "2026-06-09T10:00:00Z");

        var source  = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var matched = await source.DiscoverAsync(
            new DiscoveryFilters(null, null, new DateOnly(2026, 6, 5), 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    /// <summary>The server groups an imported session under a repository from its start payload. What
    /// Copilot recorded then outranks the checkout now: the repository may have moved, and the branch
    /// has usually changed.</summary>
    [Test]
    [Arguments("repository: acme/widget\nbranch: recorded\n", "git@github.invalid:other/checkout.git", "acme", "widget", "recorded", null)]
    [Arguments("repository: group/sub/widget\nbranch: recorded\n", null, "group/sub", "widget", "recorded", null)]
    [Arguments("branch: recorded\n", "git@github.invalid:acme/widget.git", "acme", "widget", "recorded", "git@github.invalid:acme/widget.git")]
    [Arguments("", "git@github.invalid:acme/widget.git", "acme", "widget", null, "git@github.invalid:acme/widget.git")]
    public async Task an_imported_session_starts_with_its_repository(
            string yaml, string? origin, string owner, string name, string? branch, string? remoteUrl) {
        using var tmp  = new TempDir();
        using var repo = GitRepo.Create();
        if (origin is not null) repo.Do("remote", "add", "origin", origin);

        var body = await ImportedStart(tmp, repo.Path, yaml);

        var repository = body["repository"]!.AsObject();
        await Assert.That(repository["owner"]!.GetValue<string>()).IsEqualTo(owner);
        await Assert.That(repository["repo_name"]!.GetValue<string>()).IsEqualTo(name);
        await Assert.That(repository["branch"]?.GetValue<string>()).IsEqualTo(branch);
        await Assert.That(repository["remote_url"]?.GetValue<string>()).IsEqualTo(remoteUrl);
    }

    [Test]
    public async Task a_session_outside_any_repository_starts_without_one() {
        using var tmp = new TempDir();

        var body = await ImportedStart(tmp, tmp.CreateDir("plain"), "");

        await Assert.That(body["repository"]).IsNull();
    }

    async Task<JsonObject> ImportedStart(TempDir tmp, string cwd, string extraYaml) {
        var paths = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: cwd, createdAt: "2026-06-01T10:00:00Z");
        await File.AppendAllTextAsync(paths.WorkspaceYaml(paths.SessionStateDir, Sid1), extraYaml);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var source     = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home), CancellationToken.None);

        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var start = server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/copilot");

        return JsonNode.Parse(start.RequestMessage.Body!)!.AsObject();
    }

    /// <summary>Copilot's own record is where the import places the session, so discovery lists it there: when a
    /// removed worktree leaves no folder to read, and when the folder now has another remote.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task discovery_lists_a_session_under_the_repository_copilot_recorded(bool folderHasAnotherRemote) {
        using var tmp  = new TempDir();
        using var repo = GitRepo.Create();
        repo.Do("remote", "add", "origin", "git@github.invalid:other/checkout.git");
        var paths = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: folderHasAnotherRemote ? repo.Path : tmp.PathTo("removed-worktree"), createdAt: "2026-06-01T10:00:00Z");
        await File.AppendAllTextAsync(paths.WorkspaceYaml(paths.SessionStateDir, Sid1), "repository: acme/widget\nbranch: main\n");

        using var server = WireMockServer.Start();
        ImportCommand.ImportDiscoveryResult? found = null;

        await new ImportCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), Home, TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: new TestImportTerminal().Console)
            .HandleImport(
                filterCwd:               null,
                sources:                 [new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System)],
                explicitVendorSelection: true,
                discoverOnly:            true,
                discoverJson:            true,
                onDiscovered:            r => found = r);

        await Assert.That(found!.Summary.UnmatchedCount).IsEqualTo(0);
        await Assert.That(found.Summary.Repos.Single().Owner).IsEqualTo("acme");
        await Assert.That(found.Summary.Repos.Single().Name).IsEqualTo("widget");
    }

    /// <summary>A watcher that saw the transcript rewritten sent its later lines above their file numbers, so the
    /// server watermark is not a file position: the import resumes after the file line sent under it, and numbers
    /// what it sends the way the watcher did.</summary>
    [Test]
    public async Task an_import_after_a_rewrite_resumes_after_the_file_line_the_server_took() {
        using var tmp = new TempDir();
        var paths     = Layout(tmp);
        WriteSession(paths.SessionStateDir, Sid1, cwd: "/work/a", createdAt: "2026-06-01T10:00:00Z");
        var prompts = Enumerable.Range(0, 5).Select(i =>
            $$$"""{"type":"user.message","data":{"content":"p{{{i}}}"},"id":"{{{i}}}","timestamp":"2026-06-10T20:23:50Z","parentId":null}""" + "\n");
        await File.AppendAllTextAsync(Path.Combine(paths.SessionStateDir, Sid1, "events.jsonl"), string.Concat(prompts));

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":9}"""));
        server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        // File lines from 2 on were sent six above their own number, so the server's line 9 is file line 3.
        new TranscriptRenumbering(Config.Root, server.Url!).Record(Sid1.Replace("-", ""), 2, 6);

        var source     = new CopilotImportSource(Config.Root, paths, router: new GitProviderRouter(), time: TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home), CancellationToken.None);
        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var sent = server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .SelectMany(e => JsonNode.Parse(e.RequestMessage.Body!)!["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>()))
            .ToArray();
        await Assert.That(sent).IsEquivalentTo(new[] { 10, 11 });
    }

    // The server watermark is the max persisted canonical $lineNumber, and
    // every Copilot transcript ends with lines the server normalizer skips
    // (session.shutdown + the sessionEnd hook's own hook.start/hook.end pair).
    // Classification must compare against the last RELEVANT line or complete
    // imports re-classify Partial forever.
    [Test]
    [Arguments("""{"type":"session.start","data":{"sessionId":"x","context":{"cwd":"/w"}},"id":"1","timestamp":"2026-06-10T20:23:49Z","parentId":null}""", true)]
    [Arguments("""{"type":"user.message","data":{"content":"hi","transformedContent":"<x/>hi"},"id":"2","timestamp":"2026-06-10T20:23:50Z","parentId":null}""", true)]
    [Arguments("""{"type":"user.message","data":{"content":"","attachments":[]},"id":"3","timestamp":"2026-06-10T20:23:50Z","parentId":null}""", false)]
    [Arguments("""{"type":"assistant.message","data":{"content":"ok","toolRequests":[],"outputTokens":5},"id":"4","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","toolRequests":[{"toolCallId":"t1","name":"write","arguments":{}}]},"id":"5","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","reasoningText":"thinking","toolRequests":[]},"id":"6","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","toolRequests":[]},"id":"7","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", false)]
    [Arguments("""{"type":"tool.execution_complete","data":{"toolCallId":"t1","success":true,"result":{"content":"done"}},"id":"8","timestamp":"2026-06-10T20:23:56Z","parentId":null}""", true)]
    [Arguments("""{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"write"},"id":"9","timestamp":"2026-06-10T20:23:56Z","parentId":null}""", false)]
    [Arguments("""{"type":"session.shutdown","data":{"shutdownType":"routine","totalPremiumRequests":1},"id":"10","timestamp":"2026-06-10T20:24:40Z","parentId":null}""", false)]
    [Arguments("""{"type":"hook.start","data":{"hookInvocationId":"h1","hookType":"sessionEnd","input":{}},"id":"11","timestamp":"2026-06-10T20:24:41Z","parentId":null}""", false)]
    [Arguments("""{"type":"hook.end","data":{"hookInvocationId":"h1","hookType":"sessionEnd","success":true},"id":"12","timestamp":"2026-06-10T20:24:41Z","parentId":null}""", false)]
    [Arguments("""{"type":"assistant.turn_end","data":{"turnId":"0"},"id":"13","timestamp":"2026-06-10T20:23:59Z","parentId":null}""", false)]
    [Arguments("""{"type":"session.usage_record","data":{"usage":{"model":"claude-haiku-4.5","inputTokens":5,"outputTokens":1}},"id":"14","timestamp":"2026-06-10T20:23:54Z","parentId":null}""", true)]
    [Arguments("""{"type":"session.usage_record","data":{"usage":{"inputTokens":5,"outputTokens":1}},"id":"15","timestamp":"2026-06-10T20:23:54Z","parentId":null}""", false)]
    [Arguments("""{"type":"session.usage_record","data":{"model":"claude-haiku-4.5"},"id":"16","timestamp":"2026-06-10T20:23:54Z","parentId":null}""", false)]
    [Arguments("not json", false)]
    public async Task import_relevance_mirrors_the_server_normalizer_mapping(string line, bool expected) {
        await Assert.That(CopilotImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }

    static void WriteSession(string root, string dashedSid, string? cwd, string? name = null, string? createdAt = null) {
        var dir = Path.Combine(root, dashedSid);
        Directory.CreateDirectory(dir);

        File.WriteAllText(
            Path.Combine(dir, "events.jsonl"),
            """{"type":"session.start","data":{"sessionId":"x"},"id":"11111111-1111-4111-8111-111111111111","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}"""
          + "\n");

        var yaml = $"id: {dashedSid}\n";
        if (cwd is not null) yaml       += $"cwd: {cwd}\n";
        if (name is not null) yaml      += $"name: {name}\n";
        if (createdAt is not null) yaml += $"created_at: {createdAt}\n";
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), yaml);
    }
}
