using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Scrubs terminal output captured into a transcript: ANSI escapes, C0 controls, and the U+FFFD runs
/// a non-UTF-8 byte stream leaves when it was decoded at capture.
public static partial class TerminalText {
    /// The text without escapes, controls (tab and line breaks kept) and U+FFFD runs, ends trimmed.
    /// Newlines and each line's own spacing are kept.
    public static string Clean(string s) {
        if (string.IsNullOrEmpty(s)) return "";

        // CSI and OSC first: they contain ESC, which the control sweep would otherwise split.
        var t = AnsiCsi().Replace(s, "");
        t = AnsiOsc().Replace(t, "");
        t = Control().Replace(t, "");
        t = Replacement().Replace(t, "");

        return t.Trim();
    }

    /// The first cleaned line holding a letter or digit, whitespace collapsed; null when none does.
    public static string? FirstMeaningfulLine(string s) {
        foreach (var line in Clean(s).Split('\n')) {
            if (!line.Any(char.IsLetterOrDigit)) continue;

            return Whitespace().Replace(line, " ").Trim();
        }

        return null;
    }

    // The `$` alternative takes a CSI cut off before its final byte, or the sweep would leave "[32" behind.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*(?:[@-~]|$)")]
    private static partial Regex AnsiCsi();

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)")]
    private static partial Regex AnsiOsc();

    [GeneratedRegex(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")]
    private static partial Regex Control();

    [GeneratedRegex("\\uFFFD+")]
    private static partial Regex Replacement();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
