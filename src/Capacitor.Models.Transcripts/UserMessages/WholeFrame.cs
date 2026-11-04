namespace Capacitor.Models.Transcripts.UserMessages;

/// A message made of tagged frames and nothing else: it opens with the tag, and between and after the
/// frames there is only whitespace. A last frame cut off before its close still counts; words beside a
/// complete frame make the message someone writing about it.
static class WholeFrame {
    /// <param name="text">The message, reminders removed.</param>
    /// <param name="open">The opening tag, without its `>` when the tag can carry attributes.</param>
    /// <param name="close">The closing tag.</param>
    public static bool Is(string text, string open, string close) {
        var span = text.AsSpan().Trim();
        if (!OpensWith(span, open)) return false;

        while (true) {
            var end = span.IndexOf(close, StringComparison.Ordinal);
            if (end < 0) return true;

            span = span[(end + close.Length)..].TrimStart();
            if (span.IsEmpty) return true;
            if (!OpensWith(span, open)) return false;
        }
    }

    // The tag has to end where its name does, or a longer tag that starts the same way reads as it.
    static bool OpensWith(ReadOnlySpan<char> span, string open) {
        if (!span.StartsWith(open, StringComparison.Ordinal)) return false;

        var after = span[open.Length..];

        return open[^1] == '>' || after.Length == 0 || after[0] is '>' or '/' or ' ' or '\t' or '\r' or '\n';
    }
}
