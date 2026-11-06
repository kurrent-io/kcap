using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The tag a CI event or another session's message arrives wrapped in. It only names the sender, which a
/// reader is shown by label, so wherever the words are quoted the frame is left out.
public static partial class TriggerFrame {
    /// The words inside the one frame wrapping the text, after the peer-message preamble when there is one.
    /// Any other text, words a person wrote around a frame and two frames in one message included, comes
    /// back as it is.
    public static string Unframed(string text) =>
        Frame().Match(text) is { Success: true } m && m.Groups[2].Value.Trim() is { Length: > 0 } inside ? inside : text;

    [GeneratedRegex(@"^\s*(?:Another Claude session sent a message[^\n<]*\n\s*)?<(ci-monitor-event|cross-session-message)(?:\s[^>]*)?>((?:(?!</?(?:ci-monitor-event|cross-session-message)[\s>]).)*)</\1>\s*$", RegexOptions.Singleline)]
    private static partial Regex Frame();
}
