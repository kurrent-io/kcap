namespace Capacitor.App.ViewModels;

/// Shortens a one-line detail to a character budget. Which end goes depends on what the line is:
/// a command or a path is identified by both ends — the program and the argument, the root and
/// the file name — so the middle goes, while prose is read left to right and keeps its opening.
public static class TextElision {
    public static string Middle(string text, int max) {
        if (text.Length <= max || max < 3) return text;
        var head = max / 2;
        var tail = max - head - 1;
        // A cut between the two halves of a surrogate pair renders as a replacement box; give up
        // the char on whichever side is split rather than emit one.
        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (char.IsLowSurrogate(text[^tail])) tail--;
        return string.Concat(text.AsSpan(0, head), "\u2026", text.AsSpan(text.Length - tail));
    }

    /// Drops whole folders from the middle of a path, keeping its first folder and as many of the
    /// trailing ones as fit before the file: <c>src/…/ViewModels/ToolDetail.cs</c>. A cut through a
    /// folder name leaves a fragment the reader has to decode.
    public static string Path(string text, int max) {
        if (text.Length <= max) return text;
        var sep = text.Contains('/') ? '/' : '\\';
        var parts = text.TrimEnd(sep).Split(sep);
        var first = 0;
        while (first < parts.Length - 1 && parts[first].Length == 0) first++;
        if (parts.Length - first < 3) return Middle(text, max);
        var head = string.Join(sep, parts[..(first + 1)]);
        var tail = parts[^1];
        var next = parts.Length - 2;
        while (next > first && head.Length + 3 + parts[next].Length + 1 + tail.Length <= max)
            tail = parts[next--] + sep + tail;
        var anchored = $"{head}{sep}\u2026{sep}{tail}";
        if (anchored.Length <= max) return anchored;
        var bare = $"\u2026{sep}{tail}";
        return bare.Length <= max ? bare : Middle(parts[^1], max);
    }

    public static string End(string text, int max) {
        if (text.Length <= max || max < 2) return text;
        var head = max - 1;
        if (char.IsHighSurrogate(text[head - 1])) head--;
        return string.Concat(text.AsSpan(0, head), "\u2026");
    }
}
