using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>An operator's repository pattern: an exact path, or <c>/prefix/*</c> for the directory and
/// everything under it. Both sides are resolved before they are compared, because a <c>..</c> segment or a
/// symlink is how a path outside the pattern reads as inside it; the comparison then follows the host's
/// case rule.</summary>
public static class RepoPathPattern {
    public static bool Matches(string pattern, string repoPath) {
        if (!IsPath(pattern) || !IsPath(repoPath)) return false;

        var glob = pattern.Replace('\\', '/').EndsWith("/*", StringComparison.Ordinal);
        var root = glob ? pattern[..^2] : pattern;
        if (root.Length == 0) root = "/";

        if (!TryResolve(root, out var resolvedRoot) || !TryResolve(repoPath, out var resolvedPath)) return false;

        if (string.Equals(resolvedPath, resolvedRoot, PathComparison.Comparison)) return true;
        if (!glob) return false;

        var prefix = resolvedRoot.EndsWith('/') ? resolvedRoot : resolvedRoot + "/";

        return resolvedPath.StartsWith(prefix, PathComparison.Comparison);
    }

    // Separators are folded after resolving, never before: on POSIX a backslash is a filename character.
    static bool TryResolve(string path, out string resolved) {
        var ok = CanonicalPath.TryResolve(path, out var real);
        resolved = Path.TrimEndingDirectorySeparator(real.Replace('\\', '/'));
        if (resolved.Length == 0) resolved = "/";

        return ok;
    }

    static bool IsPath(string? path) => !string.IsNullOrEmpty(path) && !path.Contains('\0');
}
