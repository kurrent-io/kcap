namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The HEAD read stands in for <c>git branch --show-current</c>, so each case is checked against what
/// git itself prints for the same checkout.
/// </summary>
public class GitRepositoryShowCurrentBranchTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_unborn_branch_reads_as_git_prints_it() {
        using var repo = GitRepo.Create();

        await Assert.That(GitRepository.ShowCurrentBranch(repo.Path)).IsEqualTo(repo.CurrentBranch);
        await Assert.That(repo.CurrentBranch).IsEqualTo("main");
    }

    [Test]
    public async Task A_nested_branch_name_reads_from_a_subdirectory() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Checkout("feature/x", create: true);
        var deep = repo.CreateDir("src", "deep");

        await Assert.That(GitRepository.ShowCurrentBranch(deep.Path)).IsEqualTo("feature/x");
        await Assert.That(repo.CurrentBranch).IsEqualTo("feature/x");
    }

    [Test]
    public async Task A_detached_head_reads_as_empty_as_git_prints_nothing() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Do("checkout", "-q", "--detach");

        await Assert.That(GitRepository.ShowCurrentBranch(repo.Path)).IsEqualTo("");
        await Assert.That(repo.CurrentBranch).IsEqualTo("");
    }

    [Test]
    public async Task A_linked_worktree_reads_its_own_branch() {
        using var repo = GitRepo.CreateWithCommit();
        var worktree = repo.AddWorktree(Tmp.PathTo("wt"), branch: "side");

        await Assert.That(GitRepository.ShowCurrentBranch(worktree.Path)).IsEqualTo(worktree.CurrentBranch);
        await Assert.That(worktree.CurrentBranch).IsEqualTo("side");
    }

    /// <summary>git follows a branch that is itself a symbolic ref to its target; the file read
    /// cannot, so it leaves the answer to git.</summary>
    [Test]
    public async Task A_branch_that_is_a_symbolic_ref_is_left_to_git() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Do("symbolic-ref", "refs/heads/alias", "refs/heads/main");
        repo.Do("symbolic-ref", "HEAD", "refs/heads/alias");

        await Assert.That(GitRepository.ShowCurrentBranch(repo.Path)).IsNull();
    }

    [Test]
    public async Task A_reftable_placeholder_head_is_left_to_git() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateFile("repo/.git/HEAD", "ref: refs/heads/.invalid\n");

        await Assert.That(GitRepository.ShowCurrentBranch(repo.Path)).IsNull();
    }

    [Test]
    public async Task A_head_outside_refs_heads_is_left_to_git() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateFile("repo/.git/HEAD", "ref: refs/remotes/origin/main\n");

        await Assert.That(GitRepository.ShowCurrentBranch(repo.Path)).IsNull();
    }

    [Test]
    public async Task No_working_tree_or_a_missing_directory_is_left_to_git() {
        await Assert.That(GitRepository.ShowCurrentBranch(Tmp.CreateDir("plain").Path)).IsNull();
        await Assert.That(GitRepository.ShowCurrentBranch(Tmp.PathTo("gone"))).IsNull();
    }
}
