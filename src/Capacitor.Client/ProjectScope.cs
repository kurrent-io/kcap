using Capacitor.Cli.Core;

namespace Capacitor.Client;

/// Selects the sessions of a set of git roots. A repository with an origin remote is matched by its
/// group key; one without is matched by path, because a server-listed row for it carries a hashed
/// `repo:local~…` key that never equals the local `path:` key.
public sealed class ProjectScope {
    readonly HashSet<string> _keys;
    readonly string[] _roots;

    ProjectScope(HashSet<string> keys, string[] roots) {
        _keys = keys;
        _roots = roots;
    }

    public bool IsEmpty => _roots.Length == 0;

    public static ProjectScope For(IReadOnlyList<string> gitRoots, RepoIdentityResolver resolver, Func<string, string> resolveMainRoot) {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(PlatformPaths.Comparer);

        foreach (var given in gitRoots) {
            var root = PlatformPaths.Normalize(given);
            if (root.Length == 0) continue;

            var main = PlatformPaths.Normalize(resolveMainRoot(root));
            roots.Add(root);
            roots.Add(main);

            var key = resolver.ForLocalRoot(main).Key;
            if (key.StartsWith("repo:", StringComparison.Ordinal)) keys.Add(key);
        }

        return new ProjectScope(keys, [.. roots]);
    }

    public bool Matches(AgentRow row) {
        if (IsEmpty) return false;

        return (row.RepoGroupKey.StartsWith("repo:", StringComparison.Ordinal) && _keys.Contains(row.RepoGroupKey))
            || LiesUnderARoot(row.RepoPath) || LiesUnderARoot(row.WorktreePath) || LiesUnderARoot(row.BorrowedFrom);
    }

    bool LiesUnderARoot(string? path) {
        if (string.IsNullOrEmpty(path)) return false;

        for (var dir = PlatformPaths.Normalize(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            foreach (var root in _roots)
                if (PlatformPaths.Comparer.Equals(dir, root)) return true;

        return false;
    }
}
