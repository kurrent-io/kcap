namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// Kiro Crew sends every prompt to `kiro-cli` wrapped in its own context (agent system prompt, runtime,
/// project, reply rules), from about 2 KB to tens of KB. The request follows a fixed marker.
public static class KiroCrewPromptEnvelope {
    const string RequestMarker = "[CURRENT USER REQUEST -- respond to this]\n";

    /// A first prompt opens with the agent system prompt, a later one with the runtime block.
    static readonly string[] EnvelopeOpeners = ["[AGENT SYSTEM PROMPT]", "[RUNTIME]"];

    /// Every envelope names the Crew interface on its runtime line; the opener alone is too common a shape
    /// to discard a prompt's text on.
    const string CrewRuntimeLine = "[RUNTIME] KiroCrew ";

    /// Where Kiro cuts a prompt it records. A longer envelope loses its request.
    public const int RecordedLimit = 32_767;

    /// The prompt is an envelope Kiro cut off before the request: the person asked something that was not recorded.
    public static bool IsCutOff(string prompt) =>
        prompt.Length >= RecordedLimit
     && (prompt.StartsWith("[AGENT SYSTEM PROMPT]", StringComparison.Ordinal) || prompt.StartsWith(CrewRuntimeLine, StringComparison.Ordinal))
     && !prompt.Contains(RequestMarker, StringComparison.Ordinal);

    /// The request inside an envelope, or the prompt unchanged when it is not one. The first marker is
    /// Crew's, so a marker the user typed stays part of their message.
    public static string Unwrap(string prompt) => RequestStart(prompt) is { } start ? prompt[start..] : prompt;

    /// <inheritdoc cref="RequestStart(ReadOnlySpan{char})"/>
    public static int? RequestStart(string prompt) => RequestStart(prompt.AsSpan());

    /// Where the request inside an envelope starts; it runs to the end. Null when the prompt is not one.
    public static int? RequestStart(ReadOnlySpan<char> prompt) {
        var opens = false;
        foreach (var opener in EnvelopeOpeners) opens |= prompt.StartsWith(opener, StringComparison.Ordinal);
        if (!opens) return null;

        var at = prompt.IndexOf(RequestMarker, StringComparison.Ordinal);
        if (at < 0) return null;

        var envelope = prompt[..at];
        if (!envelope.StartsWith(CrewRuntimeLine, StringComparison.Ordinal)
         && !envelope.Contains("\n" + CrewRuntimeLine, StringComparison.Ordinal)) return null;

        // Kept exactly as sent: a request can open with indented code.
        var start = at + RequestMarker.Length;

        return prompt[start..].IsWhiteSpace() ? null : start;
    }
}
