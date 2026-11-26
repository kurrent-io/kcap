using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// <summary>The title a root transcript's <c>session_title</c> line carries. OpenCode cannot tell a rename from its one
/// generated title, so both are a rename; its placeholder is none. No OpenCode line ends a turn.</summary>
public sealed class OpenCodeLineSignals : ILineSignalReader {
    public static readonly OpenCodeLineSignals Instance = new();

    static readonly long MinEpochMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxEpochMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    OpenCodeLineSignals() { }

    public LineSignals Read(string line, string sessionId, string? agentId) {
        if (agentId is not null || !line.Contains("\"session_title\"", StringComparison.Ordinal)) return LineSignals.None;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (!root.IsObject || root.Str("type") != "session_title"
             || HarnessTitleText.Normalize(root.Str("title")) is not { } title || OpenCodeTitleLine.IsPlaceholder(title))
                return LineSignals.None;

            return LineSignals.None with { Title = new TitleSighting("rename", title, Time(root), false) };
        } catch (JsonException) {
            return LineSignals.None;
        }
    }

    static DateTimeOffset? Time(JsonElement root) =>
        root.Num("time") is { } ms && ms >= MinEpochMs && ms <= MaxEpochMs ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
