using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

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

    Task<string?> ResolvedCommonDirAsync(string repoPath) => ResolvedCommonDirAsync(repoPath, time);

    static async Task<string?> ResolvedCommonDirAsync(string repoPath, TimeProvider time) =>
        await ResolveGitCommonDirAsync(repoPath, time) is { } common ? CanonicalPath.Resolve(common) : null;

    Task<bool> RepositoryStillAtAsync(string repoPath, string pinned, string? pinnedCommonDir) =>
        RepositoryStillAtAsync(repoPath, pinned, pinnedCommonDir, time);

    /// <summary>Ends on the path walk, with no await after it, so the git write that follows runs on what
    /// was just checked.</summary>
    static async Task<bool> RepositoryStillAtAsync(string repoPath, string pinned, string? pinnedCommonDir, TimeProvider time) =>
        string.Equals(await ResolvedCommonDirAsync(repoPath, time), pinnedCommonDir, StringComparison.Ordinal)
     && SourceStillAt(repoPath, pinned);

    /// <inheritdoc cref="RepositoryStillAtAsync(string, string, string?, TimeProvider)"/>
    async Task RefuseIfRepositoryMovedAsync(string repoPath, string pinned, string? pinnedCommonDir) {
        if (!await RepositoryStillAtAsync(repoPath, pinned, pinnedCommonDir))
            throw new InvalidOperationException($"source_repo_moved: {repoPath}");
    }

    /// <summary>A rollback writes to the source too, so one whose source has moved is skipped: the
    /// leftovers stay in the repository they were made in, never removed from another.</summary>
    async Task RollBackUnlessMovedAsync(string repoPath, string pinned, string? pinnedCommonDir, Func<Task> rollBack) {
        if (await RepositoryStillAtAsync(repoPath, pinned, pinnedCommonDir)) await rollBack();
        else LogRollbackSkipped(repoPath);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Path} no longer resolves to the repository the worktree was cut from; its rollback was skipped")]
    partial void LogRollbackSkipped(string path);
}
