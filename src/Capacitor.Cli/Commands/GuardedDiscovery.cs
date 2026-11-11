namespace Capacitor.Cli.Commands;

/// <summary>
/// All import sources share symlink resolution, real-path deduplication, and depth/cycle guards
/// so a linked or oversized session tree cannot loop or abort discovery.
/// </summary>
internal static class GuardedDiscovery {
    public static IEnumerable<string> EnumerateFiles(string root, string pattern, int maxDepth = 8, bool recursive = true) {
        if (!Directory.Exists(root)) yield break;

        var opts = new EnumerationOptions {
            RecurseSubdirectories = recursive,
            MaxRecursionDepth     = maxDepth,
            IgnoreInaccessible    = true,           // survive an inaccessible dir
            AttributesToSkip      = FileAttributes.ReparsePoint, // skip symlinked dirs/files
            ReturnSpecialDirectories = false,
        };

        // Real-path dedupe guards a symlinked dir that resolves to an already-visited tree.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerator<string> e;
        try {
            e = Directory.EnumerateFiles(root, pattern, opts).GetEnumerator();
        } catch {
            yield break; // hostile root — one exception must not abort the whole import
        }

        using (e) {
            while (true) {
                string current;
                try {
                    if (!e.MoveNext()) break;
                    current = e.Current;
                } catch {
                    // A per-entry failure (race, permission) is skipped, not fatal.
                    continue;
                }

                var real = TryResolveReal(current);
                if (seen.Add(real)) yield return current;
            }
        }
    }

    static string TryResolveReal(string path) {
        try {
            var fi = new FileInfo(path);
            return fi.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        } catch {
            return Path.GetFullPath(path);
        }
    }
}
