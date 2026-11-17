using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Folds what several handlers of one Claude hook process wrote into the single JSON object Claude reads from the
/// hook's stdout: a second object beside the first would cost the reader both. Text both name under the same key,
/// such as <c>hookSpecificOutput.additionalContext</c>, is joined; any other clash keeps the first handler's value.
/// </summary>
static class ClaudeHookOutput {
    /// <summary>The merged object, or null when no handler wrote one. A line that is not a JSON object goes to
    /// <paramref name="stderr"/> instead, where it cannot corrupt the rest.</summary>
    public static string? Merge(IEnumerable<string> outputs, TextWriter stderr) {
        JsonObject? merged = null;

        foreach (var output in outputs)
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            JsonObject? next;
            try { next = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { next = null; }

            if (next is null) {
                stderr.WriteLine(line);
                continue;
            }

            if (merged is null) merged = next;
            else Fold(merged, next);
        }

        return merged?.ToJsonString();
    }

    static void Fold(JsonObject into, JsonObject from) {
        foreach (var (key, value) in from.ToList()) {
            from.Remove(key);

            if (!into.TryGetPropertyValue(key, out var existing) || existing is null) {
                into[key] = value;
            } else if (existing is JsonObject a && value is JsonObject b) {
                Fold(a, b);
            } else if (key != "hookEventName" && Text(existing) is { } first && Text(value) is { } second && first != second) {
                into[key] = JsonString($"{first}\n\n{second}");
            }
        }
    }

    // A string assigned into a JsonObject needs type metadata NativeAOT does not keep; a parsed node does not.
    static JsonNode JsonString(string value) => JsonNode.Parse($"\"{JsonEncodedText.Encode(value)}\"")!;

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
