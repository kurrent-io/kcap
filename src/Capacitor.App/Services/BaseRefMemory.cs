using System.Collections.Frozen;

namespace Capacitor.App.Services;

/// The base refs sessions were recently started from, per machine and repository, kept in
/// <see cref="AppState.BaseRefsByRepo"/> most recent first. A key is the machine scope — "local", or
/// "remote:" plus the daemon name — then a newline, then the repository path as the launcher holds
/// it; a newline appears in neither a daemon name nor a path the launcher accepts.
public static class BaseRefMemory {
    public const int Capacity = 5;

    public static string ScopeFor(string machine, bool remote) => remote ? $"remote:{machine}" : "local";

    public static IReadOnlyList<string> Lookup(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? byRepo, string scope, string repoPath,
            IEqualityComparer<string> pathComparer) {
        if (byRepo is null) return [];

        foreach (var entry in byRepo)
            if (Matches(entry.Key, scope, repoPath, pathComparer))
                return entry.Value;

        return [];
    }

    /// Moves baseRef to the front, dropping the oldest past <see cref="Capacity"/>.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Remember(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? byRepo, string scope, string repoPath,
            IEqualityComparer<string> pathComparer, string baseRef) {
        var current = Lookup(byRepo, scope, repoPath, pathComparer);
        string[] next = [baseRef, .. current.Where(r => r != baseRef).Take(Capacity - 1)];
        return Replace(byRepo, scope, repoPath, pathComparer, next);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Forget(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? byRepo, string scope, string repoPath,
            IEqualityComparer<string> pathComparer, string baseRef) {
        var current = Lookup(byRepo, scope, repoPath, pathComparer);
        return Replace(byRepo, scope, repoPath, pathComparer, [.. current.Where(r => r != baseRef)]);
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> Replace(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? byRepo, string scope, string repoPath,
            IEqualityComparer<string> pathComparer, string[] refs) {
        var next = new Dictionary<string, IReadOnlyList<string>>();
        if (byRepo is not null)
            foreach (var entry in byRepo)
                if (!Matches(entry.Key, scope, repoPath, pathComparer))
                    next[entry.Key] = entry.Value;

        if (refs.Length > 0) next[$"{scope}\n{repoPath}"] = refs;
        return next.Count == 0 ? FrozenDictionary<string, IReadOnlyList<string>>.Empty : next;
    }

    // The path part is compared under the caller's comparer on read: System.Text.Json rebuilds the
    // dictionary with an ordinal comparer on load.
    static bool Matches(string key, string scope, string repoPath, IEqualityComparer<string> pathComparer) {
        var newline = key.IndexOf('\n');
        return newline >= 0
            && key.AsSpan(0, newline).SequenceEqual(scope)
            && pathComparer.Equals(key[(newline + 1)..], repoPath);
    }
}
