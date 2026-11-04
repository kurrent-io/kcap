using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Folds the JSONL stream <c>opencode run --format json</c> writes to stdout. The reply is the text of the last
/// step; usage is summed over every <c>step_finish</c>, reasoning tokens counted as output.
/// </summary>
sealed class OpenCodeJsonEvents {
    readonly StringBuilder _step = new();

    /// <summary>The last step's text, null when it has none.</summary>
    public string? Reply => _step.Length == 0 ? null : _step.ToString();

    /// <summary>The HTTP status of an <c>error</c> event, when it carried one.</summary>
    public int? ErrorStatus { get; private set; }

    /// <summary>The message of an <c>error</c> event; null when there was none.</summary>
    public string? ErrorMessage { get; private set; }

    public ClaudeCliUsage? Usage { get; private set; }

    public void Accept(string line) {
        if (string.IsNullOrWhiteSpace(line)) return;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "step_start":
                    _step.Clear();

                    break;
                case "text" when root.Obj("part")?.Str("text") is { } text:
                    _step.Append(text);

                    break;
                case "step_finish":
                    Usage = Add(Usage, root.Obj("part"));

                    break;
                case "error" when root.Obj("error") is { } error:
                    var data = error.Obj("data");
                    ErrorStatus  = data?.Num("statusCode") is { } status and >= 100 and <= 599 ? (int)status : null;
                    ErrorMessage = data?.Str("message") ?? error.Str("name") ?? "opencode reported an error";

                    break;
            }
        } catch (JsonException) { }
    }

    static ClaudeCliUsage Add(ClaudeCliUsage? sum, JsonElement? part) {
        sum ??= new(0, 0, 0, 0, null);

        if (part is not { } p) return sum;

        double? cost = p.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
            ? (sum.CostUsd ?? 0) + c.GetDouble()
            : sum.CostUsd;

        if (p.Obj("tokens") is not { } tokens) return sum with { CostUsd = cost };

        var cache = tokens.Obj("cache");

        return new(
            sum.InputTokens      + (tokens.Num("input") ?? 0),
            sum.OutputTokens     + (tokens.Num("output") ?? 0) + (tokens.Num("reasoning") ?? 0),
            sum.CacheReadTokens  + (cache?.Num("read")  ?? 0),
            sum.CacheWriteTokens + (cache?.Num("write") ?? 0),
            cost);
    }
}
