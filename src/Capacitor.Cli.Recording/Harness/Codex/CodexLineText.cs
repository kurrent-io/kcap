using System.Text.Json;

namespace Capacitor.Cli.Recording.Harness.Codex;

/// <summary>Text blocks of a Codex rollout line, shared by the recorder and the title generator so both
/// read a prompt the same way.</summary>
public static class CodexLineText {
    /// <summary>
    /// Whether a role:"user" text is a wrapper Codex injects rather than a prompt: the
    /// <c>environment_context</c> block, the AGENTS.md instructions, or a <c>turn_aborted</c> marker,
    /// which can land before a follow-up prompt in the same session.
    /// </summary>
    public static bool IsCodexInjectedUserPrelude(string text) =>
        text.StartsWith("<environment_context>",     StringComparison.Ordinal)
     || text.StartsWith("# AGENTS.md instructions",  StringComparison.Ordinal)
     || text.StartsWith("<turn_aborted>",            StringComparison.Ordinal);

    public static string? ExtractCodexBlockText(JsonElement payload, string blockType) {
        if (payload.Arr("content") is not { } content) return null;

        foreach (var block in content.EnumerateArray()) {
            if (block.Str("type") == blockType && block.Str("text") is { } text && !string.IsNullOrEmpty(text)) {
                return text;
            }
        }

        return null;
    }
}
