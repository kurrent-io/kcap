namespace Capacitor.App.ViewModels;

/// Repository group headers that would read the same: each gets its owner or parent folder in front,
/// so two "demo" groups show as "acme/demo" and "work/demo".
public static class RailGroupLabels {
    /// The label to show for each key. A key whose label is unique keeps it; a key whose qualifier
    /// also collides keeps its plain label, since another prefix would still not tell them apart.
    public static IReadOnlyDictionary<string, string> Disambiguate(IReadOnlyList<(string Key, string Label)> groups) {
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var same in groups.GroupBy(g => g.Label, StringComparer.OrdinalIgnoreCase)) {
            var members = same.ToList();
            if (members.Count == 1) {
                shown[members[0].Key] = members[0].Label;
                continue;
            }
            var qualified = members.Select(m => (m.Key, m.Label, Qualifier: QualifierOf(m.Key))).ToList();
            foreach (var m in qualified) {
                var unique = m.Qualifier is { Length: > 0 }
                    && qualified.Count(o => string.Equals(o.Qualifier, m.Qualifier, StringComparison.OrdinalIgnoreCase)) == 1;
                shown[m.Key] = unique ? $"{m.Qualifier}/{m.Label}" : m.Label;
            }
        }
        return shown;
    }

    // "repo:owner/name" -> owner; "path:/a/b/demo" and "daemon:<id>:/a/b/demo" -> b.
    static string? QualifierOf(string key) {
        if (key.StartsWith("repo:", StringComparison.Ordinal)) {
            var slash = key.IndexOf('/', "repo:".Length);
            return slash > "repo:".Length ? key["repo:".Length..slash] : null;
        }
        var path = key.StartsWith("path:", StringComparison.Ordinal) ? key["path:".Length..]
            : key.StartsWith("daemon:", StringComparison.Ordinal) && key.IndexOf(':', "daemon:".Length) is var colon and > 0 ? key[(colon + 1)..]
            : null;
        if (string.IsNullOrEmpty(path)) return null;
        var parent = path.TrimEnd('/', '\\');
        var cut = parent.LastIndexOfAny(['/', '\\']);
        if (cut <= 0) return null;
        var leaf = RepoLabel.Leaf(parent[..cut]);
        return leaf is { Length: > 0 } and not "—" ? leaf : null;
    }
}
