using System.Text.Json;

namespace Capacitor.Cli.Recording.Harness.Gemini;

public static class GeminiLines {
    /// <summary>
    /// True when the line maps to at least one canonical event under the
    /// server's GeminiTranscriptNormalizer: a <c>gemini</c> message (always
    /// emits thinking/text/tool events), or a <c>user</c> message carrying real
    /// text (NOT the <c>&lt;session_context&gt;</c> bootstrap and NOT a
    /// <c>functionResponse</c>-only tool-result echo). Header records and
    /// <c>$set</c> mutation ops are skipped server-side and can never advance
    /// the watermark — keep this in sync with the normalizer.
    /// </summary>
    public static bool IsImportRelevantLine(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("$set") is not null) return false;

            var type = root.Str("type");
            if (type == "gemini") return true;
            if (type != "user") return false; // header (no type) or unknown

            if (root.Str("content") is { } direct) {
                return direct.Length > 0 && !direct.StartsWith("<session_context>", StringComparison.Ordinal);
            }

            if (root.Arr("content") is { } parts) {
                foreach (var part in parts.EnumerateArray()) {
                    if (part.Str("text") is { Length: > 0 } txt
                     && !txt.StartsWith("<session_context>", StringComparison.Ordinal)) {
                        return true;
                    }
                }
            }

            return false;
        } catch {
            return false;
        }
    }
}
