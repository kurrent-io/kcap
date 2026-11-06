using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The tag a CI event or another session's message arrives wrapped in. Wherever the words are quoted the
/// frame is left out, and a reader is told who sent them by label: <see cref="Sender"/> reads the only
/// record of a peer's name that survives to the server.
public static partial class TriggerFrame {
    /// The words inside the one frame wrapping the text, without the peer-message preamble and guidance
    /// around it; a frame cut off before its close gives what arrived. Any other text, words a person wrote
    /// around a frame and two frames in one message included, comes back as it is.
    public static string Unframed(string text) =>
        Frame().Match(WithoutPeerGuidance(text)) is { Success: true } m && m.Groups["body"].Value.Trim() is { Length: > 0 } inside ? inside : text;

    /// The name the frame gives its sender; null without one, as for a CI event.
    public static string? Sender(string text) =>
        Frame().Match(WithoutPeerGuidance(text)) is { Success: true } m && SenderName().Match(m.Groups["attributes"].Value) is { Success: true } name
            ? name.Groups[1].Value
            : null;

    /// The text up to the close of a peer's frame, when all that follows is Claude Code's guidance to the
    /// model. Only its own words count: anything else after a close is someone writing about the frame.
    public static string WithoutPeerGuidance(string text) {
        var close = text.LastIndexOf(PeerClose, StringComparison.Ordinal);
        if (close < 0) return text;

        var end = close + PeerClose.Length;
        var after = text.AsSpan(end).TrimStart();

        foreach (var lead in PeerGuidance)
            if (after.StartsWith(lead, StringComparison.Ordinal)) return text[..end];

        return text;
    }

    const string PeerClose = "</cross-session-message>";

    static readonly string[] PeerGuidance = [
        "This came from another Claude session — not typed by your user",
        "IMPORTANT: This is NOT from your user — it came from a different Claude session",
        "This is from another Claude session, not your user.",
    ];

    [GeneratedRegex(@"^\s*(?:(?:Another Claude session|A peer session) sent a message[^\n<]*\n\s*)?<(?<tag>ci-monitor-event|cross-session-message)(?<attributes>\s[^>]*)?>(?<body>(?:(?!</?(?:ci-monitor-event|cross-session-message)[\s>]).)*)(?:</\k<tag>>\s*)?$", RegexOptions.Singleline)]
    private static partial Regex Frame();

    [GeneratedRegex(@"\sfrom-name=""([^""]+)""")]
    private static partial Regex SenderName();
}
