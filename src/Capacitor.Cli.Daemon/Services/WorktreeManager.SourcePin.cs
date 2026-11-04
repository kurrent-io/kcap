using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A worktree is cut from the directory its source path resolved to when creation began. The
/// caller authorized that directory, so every write re-walks the path, and every git write also confirms
/// the repository git opens there, right before it runs: a directory under an allowed tree can be swapped
/// for a link while creation awaits git.</summary>
public partial class WorktreeManager {
    static string PinSource(string repoPath) =>
        CanonicalPath.TryResolve(repoPath, out var resolved)
            ? resolved
            : throw new InvalidOperationException($"source_repo_unresolvable: {repoPath}");

    static bool SourceStillAt(string repoPath, string pinned) =>
        CanonicalPath.TryResolve(repoPath, out var now) && string.Equals(now, pinned, StringComparison.Ordinal);

    static void RefuseIfSourceMoved(string repoPath, string pinned) {
        if (!SourceStillAt(repoPath, pinned)) throw new InvalidOperationException($"source_repo_moved: {repoPath}");
    }

    async Task<string?> ResolvedCommonDirAsync(string repoPath) =>
        await ResolveGitCommonDirAsync(repoPath, time) is { } common ? CanonicalPath.Resolve(common) : null;

    /// <summary>Ends on the path walk, with no await after it, so the git write that follows runs on what
    /// was just checked.</summary>
    async Task RefuseIfRepositoryMovedAsync(string repoPath, string pinned, string? pinnedCommonDir) {
        if (!string.Equals(await ResolvedCommonDirAsync(repoPath), pinnedCommonDir, StringComparison.Ordinal))
            throw new InvalidOperationException($"source_repo_moved: {repoPath}");
        RefuseIfSourceMoved(repoPath, pinned);
    }
}
