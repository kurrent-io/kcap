using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.Client.Tests.Unit;

public class ProjectScopeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Repo(string name, string? origin) {
        Tmp.CreateFile($"{name}/.git/config", origin is null ? "[core]\n\tbare = false\n" : $"[remote \"origin\"]\n\turl = {origin}\n");
        return Tmp.GetResolvedPath(name);
    }

    static ProjectScope ScopeOf(params string[] roots) =>
        ProjectScope.For(roots, new RepoIdentityResolver(), GitRepository.ResolveMainRepoRoot);

    static AgentRow Row(AgentOrigin origin, string repoGroupKey, string? repoPath, string? worktreePath = null, string? borrowedFrom = null) => new(
        Key: $"{origin}:1", Origin: origin, Id: "1", SessionId: null, Kind: "agent", Vendor: "claude", Status: "Running",
        CreatedAt: DateTime.UtcNow, RepoPath: repoPath, Title: null, Model: null, RequesterDisplay: null,
        WorktreePath: worktreePath, WorkLocation: null, BorrowedFrom: borrowedFrom,
        MachineBadge: null, RepoGroupKey: repoGroupKey, RepoGroupLabel: "", CheckoutKey: "", CheckoutLabel: "");

    [Test]
    public async Task Origin_repo_matches_local_remote_and_server_rows_by_key() {
        var scope = ScopeOf(Repo("app", "git@github.com:acme/app.git"));

        foreach (var origin in Enum.GetValues<AgentOrigin>())
            await Assert.That(scope.Matches(Row(origin, "repo:acme/app", "/elsewhere"))).IsTrue();
    }

    [Test]
    public async Task Row_of_another_repository_does_not_match() {
        var scope = ScopeOf(Repo("app", "git@github.com:acme/app.git"));

        await Assert.That(scope.Matches(Row(AgentOrigin.Server, "repo:acme/other", "/elsewhere"))).IsFalse();
    }

    [Test]
    public async Task Worktree_root_matches_rows_of_the_main_checkout() {
        var main = Repo("main", null);
        var worktree = Tmp.GetResolvedPath("wt");
        Tmp.CreateFile("wt/.git", $"gitdir: {main}/.git/worktrees/wt\n");

        var scope = ScopeOf(worktree);

        await Assert.That(scope.Matches(Row(AgentOrigin.Local, $"path:{main}", main))).IsTrue();
    }

    [Test]
    public async Task No_origin_repository_matches_a_local_row_by_path() {
        var root = Repo("app", null);

        await Assert.That(ScopeOf(root).Matches(Row(AgentOrigin.Local, $"path:{root}", root))).IsTrue();
    }

    [Test]
    public async Task No_origin_repository_matches_a_server_row_by_path() {
        var root = Repo("app", null);

        var row = Row(AgentOrigin.Server, "repo:local~0123456789abcdef0123/app", $"{root}/src");

        await Assert.That(ScopeOf(root).Matches(row)).IsTrue();
    }

    [Test]
    public async Task Sibling_directory_sharing_a_prefix_does_not_match() {
        var root = Repo("app", null);
        var sibling = Tmp.GetResolvedPath("app-old");

        await Assert.That(ScopeOf(root).Matches(Row(AgentOrigin.Local, $"path:{sibling}", sibling))).IsFalse();
    }

    [Test]
    public async Task Server_row_with_blank_cwd_matches_only_by_key() {
        var scope = ScopeOf(Repo("app", "git@github.com:acme/app.git"));
        var noOrigin = ScopeOf(Repo("plain", null));

        await Assert.That(scope.Matches(Row(AgentOrigin.Server, "repo:acme/app", null))).IsTrue();
        await Assert.That(noOrigin.Matches(Row(AgentOrigin.Server, "repo:local~0123456789abcdef0123/plain", null))).IsFalse();
    }

    [Test]
    public async Task Root_with_a_trailing_separator_matches_the_same_rows() {
        var root = Repo("app", null);

        var scope = ScopeOf(root + Path.DirectorySeparatorChar);

        await Assert.That(scope.Matches(Row(AgentOrigin.Local, $"path:{root}", root))).IsTrue();
    }

    [Test]
    public async Task Row_matches_through_its_worktree_path() {
        var root = Repo("app", null);

        var row = Row(AgentOrigin.Local, "path:/elsewhere", "/elsewhere", worktreePath: $"{root}/wt");

        await Assert.That(ScopeOf(root).Matches(row)).IsTrue();
    }

    [Test]
    public async Task Row_matches_through_the_checkout_it_borrows() {
        var root = Repo("app", null);

        var row = Row(AgentOrigin.Local, "path:/elsewhere", "/elsewhere", worktreePath: "/elsewhere/wt", borrowedFrom: $"{root}/src");

        await Assert.That(ScopeOf(root).Matches(row)).IsTrue();
    }

    [Test]
    public async Task Worktree_of_an_origin_repository_matches_by_the_main_checkouts_key() {
        var main = Repo("main", "git@github.com:acme/app.git");
        var worktree = Tmp.GetResolvedPath("wt");
        Tmp.CreateFile("wt/.git", $"gitdir: {main}/.git/worktrees/wt\n");

        var scope = ScopeOf(worktree);

        await Assert.That(scope.Matches(Row(AgentOrigin.Server, "repo:acme/app", null))).IsTrue();
    }

    [Test]
    public async Task Blank_roots_make_an_empty_scope() {
        var scope = ScopeOf("", "  ");

        await Assert.That(scope.IsEmpty).IsTrue();
        await Assert.That(scope.Matches(Row(AgentOrigin.Local, "path:/x", "/x"))).IsFalse();
    }

    [Test, RunOn(OS.Windows)]
    public async Task Forward_slash_root_matches_a_backslash_row_on_windows() {
        var scope = ScopeOf("C:/x/app");

        await Assert.That(scope.Matches(Row(AgentOrigin.Local, "path:C:\\x\\app", "C:\\x\\app\\src"))).IsTrue();
    }

    [Test]
    public async Task No_roots_matches_nothing() {
        var scope = ScopeOf();

        await Assert.That(scope.IsEmpty).IsTrue();
        await Assert.That(scope.Matches(Row(AgentOrigin.Local, "repo:acme/app", "/x/app"))).IsFalse();
    }
}
