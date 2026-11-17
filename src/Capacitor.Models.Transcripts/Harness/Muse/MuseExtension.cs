using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Muse;

/// The `muse` extension slug, shaped as the server's records serialize: every field present, null when Muse gave
/// none. The server's read side unpacks these blocks into those records.
public static class MuseExtension {
    public const string Slug = "muse";

    /// On a tool call: the run that issued it. A spawned child nests through its own start hook, not through this.
    public static Struct ToolCall(string? runId) {
        var block = new Struct();
        block.Fields["run_id"] = runId is null ? Value.ForNull() : Value.ForString(runId);
        return block;
    }

    /// On a tool result: its outcome, read from the result text. A refusal prefix is a failure; a JSON object with
    /// an integer <c>exit_code</c> (<c>bash</c>) succeeds only on 0; anything else succeeds. Success is always
    /// written, because the server reads a block without it as failed.
    public static Struct ToolResult(string text) {
        if (text.StartsWith("tool failed:", StringComparison.Ordinal)
         || text.StartsWith("tool denied:", StringComparison.Ordinal)
         || text.StartsWith("tool approval cancelled", StringComparison.Ordinal))
            return Outcome(false, null, text);

        if (text.StartsWith('{')) {
            try {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.Prop("exit_code") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var exitCode))
                    return Outcome(exitCode == 0, exitCode, null);
            } catch (JsonException) { }
        }

        return Outcome(true, null, null);
    }

    static Struct Outcome(bool success, int? exitCode, string? reason) {
        var block = new Struct();
        block.Fields["success"]   = Value.ForBool(success);
        block.Fields["exit_code"] = exitCode is { } code ? Value.ForNumber(code) : Value.ForNull();
        block.Fields["reason"]    = reason is null ? Value.ForNull() : Value.ForString(reason);
        return block;
    }
}
