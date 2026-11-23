using System.Collections.Frozen;
using System.Globalization;

namespace Capacitor.App.ViewModels;

public static class RailGroupLabels {
    public static IReadOnlyDictionary<string, string> Disambiguate(IReadOnlyList<(string Key, string Label)> groups) {
        if (groups.Count == 0) return FrozenDictionary<string, string>.Empty;

        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Key, string Label, string[] Candidates)>();
        foreach (var same in groups.GroupBy(g => g.Label, StringComparer.OrdinalIgnoreCase)) {
            if (same.Count() == 1) {
                var group = same.First();
                shown.Add(group.Key, group.Label);
                used.Add(group.Label);
                continue;
            }
            foreach (var (key, label) in same)
                pending.Add((key, label, [.. QualifiedLabels(key, label)]));
        }

        for (var depth = 0; pending.Any(g => depth < g.Candidates.Length); depth++) {
            var candidates = pending.Where(g => depth < g.Candidates.Length)
                .GroupBy(g => g.Candidates[depth], StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates) {
                if (candidate.Skip(1).Any() || !used.Add(candidate.Key)) continue;
                shown.Add(candidate.First().Key, candidate.Key);
            }
            pending.RemoveAll(g => shown.ContainsKey(g.Key));
        }

        foreach (var (key, label, candidates) in pending.OrderBy(g => g.Key, StringComparer.Ordinal)) {
            var prefix = candidates.LastOrDefault() ?? label;
            var number = 1;
            string candidate;
            do {
                candidate = $"{prefix} ({number.ToString(CultureInfo.InvariantCulture)})";
                number++;
            } while (!used.Add(candidate));
            shown.Add(key, candidate);
        }
        return shown;
    }

    static IEnumerable<string> QualifiedLabels(string key, string label) {
        if (key.StartsWith("repo:", StringComparison.Ordinal)) {
            var slash = key.IndexOf('/', "repo:".Length);
            if (slash > "repo:".Length) yield return $"{key["repo:".Length..slash]}/{label}";
            yield break;
        }

        string path;
        string? daemon = null;
        if (key.StartsWith("path:", StringComparison.Ordinal)) {
            path = key["path:".Length..];
        } else if (key.StartsWith("daemon:", StringComparison.Ordinal) && key.IndexOf(':', "daemon:".Length) is var colon and > 0) {
            daemon = key["daemon:".Length..colon];
            path = key[(colon + 1)..];
        } else {
            yield break;
        }

        // Remote paths can use either convention, regardless of the desktop OS.
        char[] separators = path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? ['/'] : ['/', '\\'];
        var segments = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var qualified = label;
        for (var i = segments.Length - 2; i >= 0; i--) {
            qualified = $"{segments[i]}/{qualified}";
            yield return qualified;
        }
        if (daemon is { Length: > 0 }) {
            var slash = daemon.IndexOf('/');
            if (slash >= 0) yield return $"{daemon[(slash + 1)..]}/{qualified}";
            yield return $"{daemon}/{qualified}";
        }
    }
}
