using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Recording.Harness.Codex;

/// <summary>Text blocks of a Codex rollout line, shared by the recorder and the title generator so both
/// read a prompt the same way.</summary>
public static class CodexLineText {
    /// <summary>
    /// The person's words in a role:"user" message: the first <c>input_text</c> block left with text once
    /// the context Codex injects ahead of a prompt is stripped. Null when the message holds only that
    /// context. The marker list is <see cref="CodexPrelude"/>'s, so a block Codex adds is learnt in one place.
    /// </summary>
    public static string? ExtractCodexUserPrompt(JsonElement payload) {
        if (payload.Arr("content") is not { } content) return null;

        foreach (var block in content.EnumerateArray()) {
            if (block.Str("type") != "input_text" || block.Str("text") is not { Length: > 0 } text) continue;
            if (CodexPrelude.StripLeading(text).Trim() is { Length: > 0 } prompt) return prompt;
        }

        return null;
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
