using System.Globalization;

namespace Capacitor.App.ViewModels;

/// Splits a title on every case-insensitive occurrence of a query, folding case by the user's own
/// culture: a title is their language, so a Turkish user's "i" finds "İ".
public static class TitleMatch {
    public static IReadOnlyList<SnippetRun> Split(string? text, string? filter) {
        if (string.IsNullOrEmpty(text)) return [];
        var query = filter?.Trim() ?? "";
        if (query.Length == 0) return [new SnippetRun(text, false)];

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var runs = new List<SnippetRun>();
        var at = 0;
        while (at < text.Length) {
            // The matched length can differ from the query's under culture folding.
            var found = compare.IndexOf(text.AsSpan(at), query, CompareOptions.IgnoreCase, out var length);
            if (found < 0 || length == 0) break;
            if (found > 0) runs.Add(new SnippetRun(text.Substring(at, found), false));
            runs.Add(new SnippetRun(text.Substring(at + found, length), true));
            at += found + length;
        }
        if (at < text.Length) runs.Add(new SnippetRun(text[at..], false));
        return runs;
    }
}
