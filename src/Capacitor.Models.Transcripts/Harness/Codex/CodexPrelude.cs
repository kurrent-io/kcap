using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// The context Codex writes into the user role ahead of a turn: plugin recommendations, AGENTS.md
/// instructions, the environment, permissions, an aborted-turn note, in any order.
public static partial class CodexPrelude {
    static readonly string[] Markers = [
        "<recommended_plugins>", "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>",
        "<permissions instructions>",
    ];

    /// The text after the leading prelude blocks. A block left unterminated swallows the rest, so a
    /// truncated prelude never becomes a prompt.
    public static string StripLeading(string text) {
        var rest = LeadingPrelude().Replace(text, "");

        foreach (var marker in Markers)
            if (rest.StartsWith(marker, StringComparison.Ordinal)) return "";

        return rest;
    }

    public static bool StartsWithPrelude(string? text) {
        if (text is null) return false;

        var t = text.AsSpan().TrimStart();
        foreach (var marker in Markers)
            if (t.StartsWith(marker, StringComparison.Ordinal)) return true;

        return false;
    }

    /// The message is nothing but a prelude.
    public static bool IsMatch(string? text) => !string.IsNullOrWhiteSpace(text) && StripLeading(text).Length == 0;

    /// Codex marks each content item with a kind; a part whose kind is not a `user.` one is its own context.
    public static bool HasHarnessPart(IReadOnlyList<string> contentItemKinds) =>
        contentItemKinds.Any(k => !k.StartsWith("user.", StringComparison.Ordinal));

    /// The policy judge's reading, which the server's judge shares and both sides pin with
    /// `codex-user-lines.jsonl`: with content kinds, any part not the person's; without, any message
    /// opening with a marker, even with the person's words after it.
    public static bool IsPrelude(string text, IReadOnlyList<string>? contentItemKinds) =>
        contentItemKinds is { Count: > 0 } kinds ? HasHarnessPart(kinds) : StartsWithPrelude(text);

    public static bool IsHuman(string text, IReadOnlyList<string>? contentItemKinds) => !IsPrelude(text, contentItemKinds);

    [GeneratedRegex("""
                    ^\s*(?:(?:<recommended_plugins>[\s\S]*?</recommended_plugins>
                            |\#\ AGENTS\.md\ instructions[^\n]*\s*<INSTRUCTIONS>[\s\S]*?</INSTRUCTIONS>
                            |<environment_context>[\s\S]*?</environment_context>
                            |<user_instructions>[\s\S]*?</user_instructions>
                            |<permissions\ instructions>[\s\S]*?</permissions\ instructions>
                            |<turn_aborted>[\s\S]*?</turn_aborted>
                            )\s*)*
                    """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LeadingPrelude();
}
