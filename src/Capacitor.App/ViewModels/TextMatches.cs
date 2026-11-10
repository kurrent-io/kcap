using System.Globalization;

namespace Capacitor.App.ViewModels;

/// Every case-insensitive occurrence of a query, folding case by the user's own culture, so a
/// Turkish user's "i" finds "İ". The matched length can differ from the query's under that folding.
public static class TextMatches {
    public static IReadOnlyList<(int Start, int Length)> Find(string? text, string? query) {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return [];
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        List<(int, int)>? found = null;
        var at = 0;
        while (at < text.Length) {
            var index = compare.IndexOf(text.AsSpan(at), query, CompareOptions.IgnoreCase, out var length);
            if (index < 0 || length == 0) break;
            (found ??= []).Add((at + index, length));
            at += index + length;
        }
        return found is null ? [] : found;
    }

    public static int Count(string? text, string? query) => Find(text, query).Count;
}
