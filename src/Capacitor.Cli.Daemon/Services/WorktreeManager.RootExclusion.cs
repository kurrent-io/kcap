using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    static readonly Lock RootExclusionLock = new();

    /// <summary>Keeps the daemon's worktrees under <paramref name="repoPath"/> out of that checkout's status, through
    /// the repository's own <c>info/exclude</c>. Best effort: a launch never fails over it, and a source that no
    /// longer resolves to <paramref name="pinned"/> and <paramref name="pinnedCommonDir"/> is left untouched.</summary>
    async Task ExcludeWorktreeRootAsync(string repoPath, string pinned, string? pinnedCommonDir) {
        try {
            if (await ResolveGitCommonDirAsync(repoPath, time) is not { } commonDir) return;

            var prefix = await RunGitCaptureResult(
                repoPath, GitTimeout, time, sourceReadOnly: false, config: [], "rev-parse", "--show-prefix");
            if (prefix.ExitCode != 0 || WorktreeRootExclusion.Pattern(prefix.Stdout.TrimEnd('\r', '\n')) is not { } pattern)
                return;

            if (!string.Equals(await ResolvedCommonDirAsync(repoPath), pinnedCommonDir, StringComparison.Ordinal)
             || !SourceStillAt(repoPath, pinned)) {
                LogRootExclusionSkipped(repoPath);
                return;
            }

            lock (RootExclusionLock) WorktreeRootExclusion.Apply(Path.Combine(commonDir, "info", "exclude"), pattern);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or Win32Exception) {
            LogRootExclusionFailed(ex, repoPath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not add the worktree directory under {Path} to the repository's info/exclude; git status will list it")]
    partial void LogRootExclusionFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Path} no longer resolves to the repository the worktree was cut from; its info/exclude was left alone")]
    partial void LogRootExclusionSkipped(string path);
}
