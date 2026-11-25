using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    /// <summary>The repository whose agent worktree <paramref name="worktreePath"/> was: only a path directly under an
    /// existing repository's <c>.capacitor/worktrees</c> is one the daemon may check out again.</summary>
    public static string? RestorableSource(string worktreePath) {
        var path = Path.TrimEndingDirectorySeparator(worktreePath);
        var repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)));

        return repo is not null && Path.Combine(repo, ".capacitor", "worktrees", Path.GetFileName(path)) == path && Directory.Exists(repo)
            ? repo
            : null;
    }

    /// <summary>Checks a gone agent worktree of <paramref name="repoPath"/> out again at <paramref name="worktreePath"/>, as a
    /// hand-off would: on <paramref name="branch"/> when it is a local branch no worktree has checked out, otherwise
    /// detached at the repository's HEAD, as resuming a session in one's own checkout finds whatever is there.</summary>
    public async Task RestoreAsync(string repoPath, string worktreePath, string? branch, CancellationToken ct) {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(worktreePath));

        if (!string.IsNullOrWhiteSpace(branch) && await IsValidRefNameAsync(repoPath, branch)
         && await ReadLocalBranchAsync(repoPath, branch) is { Tip: { } tip, CheckedOut: false }) {
            await CreateHandoffAsync(repoPath, new(HandoffSource.Branch, HandoffCheckout.CheckoutBranch, tip, branch, HandoffDetachReason.None, tip), name, ct);

            return;
        }

        var head = (await RunGitCapture(repoPath, GitTimeout, time, true, "rev-parse", "HEAD")).Trim();
        await CreateHandoffAsync(repoPath, new(HandoffSource.Branch, HandoffCheckout.Detached, head, branch, HandoffDetachReason.BranchAbsent, null), name, ct);
    }
}
