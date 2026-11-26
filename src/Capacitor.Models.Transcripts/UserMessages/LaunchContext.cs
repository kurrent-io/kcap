namespace Capacitor.Models.Transcripts.UserMessages;

/// Context kcap appends to a person's launch prompt for the agent to read. It closes the message, so
/// what precedes it is the ask a chat shows.
public static class LaunchContext {
    const string Open  = "<kcap-launch-context>";
    const string Close = "</kcap-launch-context>";

    public static string Wrap(string context) => $"{Open}\n{context}\n{Close}";

    /// The length of the ask ahead of a launch context that closes the text; null when none does.
    internal static int? AskLength(ReadOnlySpan<char> text) {
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith(Close, StringComparison.Ordinal)) return null;

        var open = trimmed.LastIndexOf(Open, StringComparison.Ordinal);
        return open < 0 ? null : text[..open].TrimEnd().Length;
    }
}
