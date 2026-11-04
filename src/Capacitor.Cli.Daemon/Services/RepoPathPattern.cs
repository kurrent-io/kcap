using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>An operator's repository pattern: an exact path, or <c>/prefix/*</c> for the directory and
/// everything under it. Both sides are resolved before they are compared, because a <c>..</c> segment or a
/// symlink is how a path outside the pattern reads as inside it; the comparison then follows the host's
/// case rule. A relative pattern or path matches nothing: it would mean whatever the daemon's working
/// directory happens to be.</summary>
public static class RepoPathPattern {
    public static bool IsGlob(string pattern) =>
        pattern.EndsWith("/*", StringComparison.Ordinal)
     || (OperatingSystem.IsWindows() && pattern.EndsWith("\\*", StringComparison.Ordinal));

    public static bool Matches(string pattern, string repoPath) {
        if (!IsPath(pattern) || !IsPath(repoPath)) return false;

        var glob = IsGlob(pattern);
        var root = glob ? pattern[..^2] : pattern;
        if (root.Length == 0) root = "/";
        if (OperatingSystem.IsWindows()) {
            root     = WindowsPathSpelling.Fold(root);
            repoPath = WindowsPathSpelling.Fold(repoPath);
        }
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(repoPath)) return false;

        if (!TryResolve(root, out var resolvedRoot) || !TryResolve(repoPath, out var resolvedPath)) return false;

        if (string.Equals(resolvedPath, resolvedRoot, PathComparison.Comparison)) return true;
        if (!glob) return false;

        var prefix = Path.EndsInDirectorySeparator(resolvedRoot) ? resolvedRoot : resolvedRoot + Path.DirectorySeparatorChar;

        return resolvedPath.StartsWith(prefix, PathComparison.Comparison);
    }

    // Separators are unified on Windows only: on POSIX a backslash is a filename character, so
    // `/allowed\secret` is one directory beside `/allowed`, not one inside it.
    static bool TryResolve(string path, out string resolved) {
        var ok = CanonicalPath.TryResolve(path, out var real);
        if (OperatingSystem.IsWindows()) {
            real = real.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (WindowsLongPath.TryExpand(real, out var expanded)) real = expanded;
        }
        resolved = Path.TrimEndingDirectorySeparator(real);

        return ok;
    }

    static bool IsPath(string? path) => !string.IsNullOrEmpty(path) && !path.Contains('\0');
}
