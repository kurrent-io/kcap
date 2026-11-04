namespace Capacitor.Models.Transcripts.UserMessages;

/// A message made of one tagged frame: it opens with the tag, and nothing but whitespace follows the
/// frame's last closing tag. A frame cut off before its close still counts; words after a complete
/// frame make the message someone writing about it.
static class WholeFrame {
    /// <param name="text">The message, reminders removed.</param>
    /// <param name="open">The opening tag, without its `>` when the tag can carry attributes.</param>
    /// <param name="close">The closing tag.</param>
    public static bool Is(string text, string open, string close) {
        var span = text.AsSpan().Trim();
        if (!span.StartsWith(open, StringComparison.Ordinal)) return false;

        // The tag has to end where its name does, or a longer tag that starts the same way reads as it.
        var after = span[open.Length..];
        if (open[^1] != '>' && after.Length > 0 && after[0] is not ('>' or '/' or ' ' or '\t' or '\r' or '\n')) return false;

        var end = span.LastIndexOf(close, StringComparison.Ordinal);

        return end < 0 || span[(end + close.Length)..].IsWhiteSpace();
    }
}
