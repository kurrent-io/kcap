namespace Capacitor.App.ViewModels;

/// Splits a title on every occurrence of a query, as <see cref="TextMatches"/> finds them.
public static class TitleMatch {
    public static IReadOnlyList<SnippetRun> Split(string? text, string? filter) {
        if (string.IsNullOrEmpty(text)) return [];
        var query = filter?.Trim() ?? "";
        if (query.Length == 0) return [new SnippetRun(text, false)];

        var runs = new List<SnippetRun>();
        var at = 0;
        foreach (var (start, length) in TextMatches.Find(text, query)) {
            if (start > at) runs.Add(new SnippetRun(text[at..start], false));
            runs.Add(new SnippetRun(text.Substring(start, length), true));
            at = start + length;
        }
        if (at < text.Length) runs.Add(new SnippetRun(text[at..], false));
        return runs;
    }
}
