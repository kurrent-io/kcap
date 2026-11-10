using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The repositories a daemon tells the server it can host, as concrete repository roots: a
/// consumer compares one path with another rather than re-deriving the allowlist's rules. A wildcard
/// allowlist entry (<c>…/*</c>) authorizes everything under its prefix, so it is advertised as the
/// repositories found there, never as the prefix itself, which is not a repository.</summary>
static class AdvertisedRepoPaths {
    /// <summary>How deep below a wildcard prefix repositories are looked for: <c>repos/name</c> and
    /// <c>repos/owner/name</c>. A repository found is not searched further.</summary>
    const int WildcardDepth = 2;

    /// <summary>A bound on one prefix's contribution, so a prefix over a large tree cannot swell
    /// every registration.</summary>
    const int WildcardLimit = 500;

    /// <summary>A bound on the directory entries one prefix's search reads, skipped ones included, so a
    /// prefix over a large tree with few repositories cannot hold up a registration either.</summary>
    const int EntryLimit = 20_000;

    /// <summary>Persisted paths first, in their own order, then the allowlist's: a literal entry exactly
    /// as written, since the allowlist matches it as that string, and a wildcard entry as the
    /// repositories found under it. A persisted path or literal entry that is not a directory now is
    /// left out and returned in <c>Missing</c> instead: <c>repos.json</c> keeps a deleted clone, and an
    /// unmounted volume's repositories come back when it does.</summary>
    public static (string[] Paths, string[] Missing) Merge(IReadOnlyList<string> persisted, IReadOnlyList<string> allowed) {
        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(comparer);
        var merged = new List<string>();
        var missing = new List<string>();

        void AddChecked(string path) {
            if (path.Length == 0 || !seen.Add(path)) return;
            (Directory.Exists(path) ? merged : missing).Add(path);
        }

        foreach (var path in persisted) AddChecked(path);

        foreach (var entry in allowed) {
            if (RepoPathPattern.IsGlob(entry))
                merged.AddRange(RepositoriesUnder(entry[..^2] + Path.DirectorySeparatorChar).Where(seen.Add));
            else
                AddChecked(entry);
        }

        return ([.. merged], [.. missing]);
    }

    /// <summary>The prefix itself when it is a repository, else the repositories below it, in name order,
    /// each as found: a linked worktree is advertised as itself, so a launch chosen from the list lands in
    /// the checkout that was found.</summary>
    internal static IEnumerable<string> RepositoriesUnder(string prefix) {
        // Trimmed but never past a filesystem root: "/" and "C:\" stay roots.
        var root = Path.TrimEndingDirectorySeparator(prefix);
        // The allowlist compares a launch's absolute path with the entry as written, so a relative
        // entry admits nothing that could be found under it.
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return [];
        if (IsRepository(root)) return [root];

        var found = new List<string>();
        var budget = EntryLimit;
        Walk(root, 1);
        return found;

        void Walk(string directory, int depth) {
            foreach (var child in Children(directory, ref budget)) {
                if (found.Count >= WildcardLimit) return;
                if (IsRepository(child)) found.Add(child);
                else if (depth < WildcardDepth) Walk(child, depth + 1);
            }
        }
    }

    /// <summary>A directory's visible child directories in name order, every entry read charged to the
    /// budget, files included. One with more entries than the budget has left contributes none, so what
    /// is advertised never depends on the order the filesystem lists entries in.</summary>
    static List<string> Children(string directory, ref int budget) {
        var children = new List<string>();
        try {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos()) {
                if (--budget < 0) return [];
                // A launch is authorized on its resolved path, which a link leads out of the prefix.
                if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !entry.Name.StartsWith('.')) children.Add(entry.FullName);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
        children.Sort(StringComparer.Ordinal);
        return children;
    }

    static bool IsRepository(string directory) {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }
}
