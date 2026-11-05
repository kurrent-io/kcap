using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The message Claude Code delivers on the user channel when another agent in the session writes to
/// it, a subagent's final report among them. The words are the agent's, never the user's.
public static partial class AgentMessageParser {
    /// <param name="From">The sending agent's id, which its own stream is keyed by.</param>
    /// <param name="IsHandback">The agent's final report, which ends its run.</param>
    /// <param name="Body">The message without the harness frame and the report's indentation.</param>
    public readonly record struct AgentMessage(string? From, bool IsHandback, string Body);

    // The preamble varies ("…sent a message:", "…sent a message while you were working:"), and a
    // message queued while the session was busy carries none.
    const string Preamble     = "Another Claude session sent a message";
    const string OpenTag      = "<agent-message";
    const string CloseTag     = "</agent-message>";
    const string HandbackMark = "[Subagent hand-back]";
    const string ReportMark   = "The report follows:";

    // Every line of a report is indented, so a line that looks like the frame cannot be forged from inside it.
    const string ReportIndent = "  ";

    /// True when the message opens with the tag, or with the preamble and then the tag. Words of its own
    /// before them make it someone writing about the frame.
    public static bool IsMatch(string? text) {
        if (text is null) return false;

        var span = text.AsSpan().TrimStart();
        if (span.StartsWith(Preamble, StringComparison.Ordinal)) {
            var colon = span.IndexOf(':');
            var line = span.IndexOf('\n');
            if (colon < 0 || (line >= 0 && colon > line)) return false;

            span = span[(colon + 1)..].TrimStart();
        }

        if (!span.StartsWith(OpenTag, StringComparison.Ordinal)) return false;

        // The tag has to end where the name ends, or <agent-messages> reads as one.
        var after = span[OpenTag.Length..];

        return after.Length == 0 || after[0] is '>' or ' ' or '\t' or '\r' or '\n';
    }

    /// Returns exactly <see cref="IsMatch"/>. A payload cut off before its closing tag still parses.
    public static bool TryParse(string? text, out AgentMessage result) {
        if (!IsMatch(text)) {
            result = default;
            return false;
        }

        var open = text!.IndexOf(OpenTag, StringComparison.Ordinal);
        var tag = Tag().Match(text, open);
        var from = tag.Success && tag.Groups[1].Value.Trim() is { Length: > 0 } id ? id : null;

        var start = tag.Success ? tag.Index + tag.Length : text.Length;
        // The last one: a report can quote the tag, and the harness writes only one of its own.
        var close = text.LastIndexOf(CloseTag, StringComparison.Ordinal);
        if (close < start) close = -1;
        var inner = text[start..(close < 0 ? text.Length : close)];

        var handback = inner.AsSpan().TrimStart().StartsWith(HandbackMark, StringComparison.Ordinal);
        if (handback && inner.IndexOf(ReportMark, StringComparison.Ordinal) is var mark and >= 0)
            inner = Unindent(inner[(mark + ReportMark.Length)..]);

        result = new(from, handback, inner.Trim());
        return true;
    }

    static string Unindent(string report) {
        var lines = report.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith(ReportIndent, StringComparison.Ordinal))
                lines[i] = lines[i][ReportIndent.Length..];

        return string.Join('\n', lines);
    }

    [GeneratedRegex("""<agent-message(?:\s+from="([^"]*)")?[^>]*>""")]
    private static partial Regex Tag();
}
