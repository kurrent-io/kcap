using Capacitor.Cli.Core;

namespace Capacitor.Client;

/// Last path segment of a repo path, shared by every surface that names a repository. The status
/// wire says which path is a worktree, so a path's shape is never read as evidence of one.
public static class RepoLabel {
    static readonly char[] WindowsSeparators = ['\\', '/'];

    public static string Leaf(string? repoPath) => repoPath switch {
        null                                                               => "—",
        _ when !OperatingSystem.IsWindows() && IsWindowsShaped(repoPath) => WindowsLeaf(repoPath),
        _                                                                  => PlatformPaths.Leaf(repoPath),
    };

    /// A remote daemon advertises paths in its own OS's form, and this host's path rules cannot split a
    /// Windows one. A Unix path always starts with '/', so a backslash inside one stays part of its name.
    static bool IsWindowsShaped(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
     || path is [_, ':', ..] && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] is '\\' or '/');

    // A drive or share root has no leaf, as Path.GetFileName reports on Windows.
    static string WindowsLeaf(string path) {
        var trimmed  = path.TrimEnd(WindowsSeparators);
        var segments = trimmed.Split(WindowsSeparators, StringSplitOptions.RemoveEmptyEntries);
        var isRoot   = trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? segments.Length <= 2 : segments.Length <= 1;

        return isRoot ? "" : segments[^1];
    }
}
