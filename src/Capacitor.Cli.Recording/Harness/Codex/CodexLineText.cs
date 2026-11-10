using System.Text;
using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Recording.Harness.Codex;

/// <summary>Text blocks of a Codex rollout line, shared by the recorder and the title generator so both
/// read a prompt the same way.</summary>
public static class CodexLineText {
    /// <summary>
    /// The person's words in a role:"user" message: every <c>input_text</c> block, in order and joined by a
    /// newline as the transcript projection joins them, each with the context Codex injects ahead of a prompt
    /// stripped. Null when the message holds only that context. The marker list is <see cref="CodexPrelude"/>'s,
    /// so a block Codex adds is learnt in one place.
    /// </summary>
    public static string? ExtractCodexUserPrompt(JsonElement payload) {
        if (payload.Arr("content") is not { } content) return null;

        StringBuilder? prompt = null;

        foreach (var block in content.EnumerateArray()) {
            if (block.Str("type") != "input_text" || block.Str("text") is not { Length: > 0 } text) continue;
            if (CodexPrelude.StripLeading(text).Trim() is not { Length: > 0 } words) continue;

            if (prompt is null) prompt = new StringBuilder(words);
            else prompt.Append('\n').Append(words);
        }

        return prompt?.ToString();
    }

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
