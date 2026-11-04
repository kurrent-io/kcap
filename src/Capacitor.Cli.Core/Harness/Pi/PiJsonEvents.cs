using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Folds the JSONL stream <c>pi -p --mode json</c> writes to stdout. The outcome is read from the final
/// assistant message of <c>agent_end</c>, since Pi exits 0 whether or not the provider answered. Usage is
/// summed over every assistant message; before <c>agent_end</c> it is what finished messages reported.
/// </summary>
sealed class PiJsonEvents {
    /// <summary>The final assistant message's text, null when it has none.</summary>
    public string? Reply { get; private set; }

    /// <summary>The final assistant message's <c>stopReason</c> (<c>stop</c>, <c>error</c>, …); null until <c>agent_end</c>.</summary>
    public string? StopReason { get; private set; }

    public string? ErrorMessage { get; private set; }

    public ClaudeCliUsage? Usage { get; private set; }

    public void Accept(string line) {
        if (string.IsNullOrWhiteSpace(line)) return;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "message_end" when root.Obj("message") is { } message && message.Str("role") == "assistant":
                    Usage = Add(Usage, message);

                    break;
                case "agent_end" when root.Arr("messages") is { } messages:
                    AcceptAgentEnd(messages);

                    break;
            }
        } catch (JsonException) { }
    }

    void AcceptAgentEnd(JsonElement messages) {
        ClaudeCliUsage? usage = null;
        JsonElement?    last  = null;

        foreach (var message in messages.EnumerateArray()) {
            if (message.Str("role") != "assistant") continue;

            usage = Add(usage, message);
            last  = message;
        }

        Usage = usage;

        if (last is not { } final) return;

        StopReason   = final.Str("stopReason");
        ErrorMessage = final.Str("errorMessage");
        Reply        = TextOf(final);
    }

    static string? TextOf(JsonElement message) {
        if (message.Arr("content") is not { } content) return null;

        var text = new StringBuilder();

        foreach (var part in content.EnumerateArray()) {
            if (part.Str("type") == "text" && part.Str("text") is { } value) text.Append(value);
        }

        return text.Length == 0 ? null : text.ToString();
    }

    static ClaudeCliUsage Add(ClaudeCliUsage? sum, JsonElement message) {
        sum ??= new(0, 0, 0, 0, null);

        if (message.Obj("usage") is not { } usage) return sum;

        double? cost = usage.Obj("cost") is { } c && c.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number
            ? (sum.CostUsd ?? 0) + total.GetDouble()
            : sum.CostUsd;

        return new(
            sum.InputTokens      + (usage.Num("input")      ?? 0),
            sum.OutputTokens     + (usage.Num("output")     ?? 0),
            sum.CacheReadTokens  + (usage.Num("cacheRead")  ?? 0),
            sum.CacheWriteTokens + (usage.Num("cacheWrite") ?? 0),
            cost);
    }
}
