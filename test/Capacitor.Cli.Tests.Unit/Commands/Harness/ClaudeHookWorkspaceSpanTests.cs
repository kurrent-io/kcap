using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.Workspace;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>Claude's turn hooks open and close a workspace span over a real repository and post it.</summary>
[ParallelLimiter<SubprocessLimit>]
public class ClaudeHookWorkspaceSpanTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [GitRepo] public required GitRepo Repo { get; init; }

    const string Session = "3fc0ace3-409c-4534-a7fb-e0bfecf274f7";

    readonly WireMockServer _server = WireMockServer.Start();

    // Never moves, so no snapshot or drain window can run out on a loaded host.
    readonly FakeTimeProvider _time = new();

    public void Dispose() => _server.Stop();

    [Before(Test)]
    public void Answer() {
        _server.Given(Request.Create().WithPath("/hooks/workspace-changes").UsingPost()).RespondWith(Response.Create().WithStatusCode(202));
        Repo.CreateFile("README.md", "seed");
        Repo.CommitAll("seed");
    }

    Task<int> HookAsync(string payload, ProfileContext? profiles = null) {
        profiles ??= Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        return new ClaudeHookCommand(Config.Root, profiles, new HookClock(_time), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, http,
                TestWatchers.For(Config.Root, profiles, http), SystemProcessStarter.Instance, router: new GitProviderRouter(),
                workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .HandleWorkspaceSpanCore(new StringReader(payload), new WorkspaceSpanHook(Config.Root, profiles, http, _time));
    }

    static string Hook(string eventName, string cwd, string extra = "") =>
        $$"""{"hook_event_name":"{{eventName}}","session_id":"{{Session}}","cwd":{{JsonSerializer.Serialize(cwd)}}{{extra}}}""";

    List<WorkspaceChangesRequest> Posted() => _server.LogEntries
        .Where(e => e.RequestMessage.Path == "/hooks/workspace-changes")
        .Select(e => JsonSerializer.Deserialize(e.RequestMessage.Body!, WorkspaceJsonContext.Default.WorkspaceChangesRequest)!)
        .ToList();

    static string Toplevel(GitRepo repo) => Path.GetFullPath(repo.Do("rev-parse", "--show-toplevel").Text);

    [Test]
    public async Task A_prompt_opens_a_span_and_stop_posts_what_the_turn_changed() {
        await HookAsync(Hook("UserPromptSubmit", Repo.Path, ""","prompt":"go" """));
        Repo.CreateFile("made-by-a-script.txt", "x");
        await HookAsync(Hook("Stop", Repo.Path));

        var posted = Posted().Single();
        await Assert.That(posted.SessionId).IsEqualTo(Session.Replace("-", ""));
        await Assert.That(posted.RepoRoot).IsEqualTo(Toplevel(Repo));
        await Assert.That(posted.Changes).IsEquivalentTo([new WorkspaceChange("made-by-a-script.txt", "create")]);
    }

    /// <summary>The fixture is a real EnterWorktree PostToolUse; its <c>cwd</c> is set to the tree the turn
    /// left, so only the tool's response can name where it went.</summary>
    [Test]
    public async Task Entering_a_worktree_opens_a_span_on_its_destination_and_stop_posts_both_trees() {
        var b       = Repo.AddWorktree(Repo.PathTo("..", Path.GetFileName(Repo.Path) + "-b"), "b");
        var fixture = File.ReadAllText(Path.Combine(RepoRoot(), "test", "fixtures", "claude", "enter-worktree-post-tool-use.json"))
            .Replace("$TREE_A", JsonEncoded(Repo.Path)).Replace("$TREE_B", JsonEncoded(b.Path));
        try {
            await HookAsync(Hook("UserPromptSubmit", Repo.Path));
            Repo.CreateFile("before-switch.txt", "x");
            await HookAsync(fixture);
            b.CreateFile("after-switch.txt", "x");
            await HookAsync(Hook("Stop", b.Path));

            var byRoot = Posted().ToDictionary(p => p.RepoRoot, p => p.Changes.Single().Path);
            await Assert.That(byRoot).IsEquivalentTo(new Dictionary<string, string> {
                [Toplevel(Repo)] = "before-switch.txt", [Toplevel(b)] = "after-switch.txt",
            });
        } finally {
            Repo.Try("worktree", "remove", "--force", b.Path);
        }
    }

    [Test]
    public async Task A_subagents_hooks_take_no_span() {
        await HookAsync(Hook("PostToolUse", Repo.Path,
            $$""","agent_id":"a1","tool_name":"EnterWorktree","tool_response":{"worktreePath":{{JsonSerializer.Serialize(Repo.Path)}}}"""));
        Repo.CreateFile("subagent.txt", "x");
        await HookAsync(Hook("Stop", Repo.Path));

        await Assert.That(Posted()).IsEmpty();
    }

    [Test]
    public async Task Entering_a_worktree_the_profile_excludes_opens_no_span_there() {
        var b        = Repo.AddWorktree(Repo.PathTo("..", Path.GetFileName(Repo.Path) + "-excluded"), "excluded");
        var profiles = Resolutions.Of(new Profile { ServerUrl = _server.Url!, ExcludedPaths = [b.Path] });
        try {
            await HookAsync(Hook("PostToolUse", Repo.Path,
                $$""","tool_name":"EnterWorktree","tool_response":{"worktreePath":{{JsonSerializer.Serialize(b.Path)}}}"""), profiles);
            b.CreateFile("private.txt", "x");
            // The turn's Stop can still name the tree it started in.
            await HookAsync(Hook("Stop", Repo.Path), profiles);

            await Assert.That(Posted()).IsEmpty();
        } finally {
            Repo.Try("worktree", "remove", "--force", b.Path);
        }
    }

    /// <summary>A prompt waits on its hook, so the span it opens never waits on the server; a span the
    /// prompt closed is left for the turn's Stop to post.</summary>
    [Test]
    public async Task A_prompt_posts_nothing_even_with_spans_waiting_in_the_spool() {
        await HookAsync(Hook("UserPromptSubmit", Repo.Path));
        Repo.CreateFile("first-turn.txt", "x");

        await HookAsync(Hook("UserPromptSubmit", Repo.Path));

        await Assert.That(Posted()).IsEmpty();
    }

    static string JsonEncoded(string path) => JsonSerializer.Serialize(path)[1..^1];

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException($"no repository root above {here}");
    }
}
