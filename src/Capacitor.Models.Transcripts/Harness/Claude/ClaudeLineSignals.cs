using System.Text;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

public sealed class ClaudeLineSignals : ILineSignalReader {
    public static readonly ClaudeLineSignals Instance = new();

    const int MaxTitleLength = 200;

    public LineSignals Read(string line, string sessionId, string? agentId) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (!root.IsObject) return LineSignals.None;

            var isRoot = agentId is null;

            return new LineSignals(
                root.Str("uuid"),
                root.Str("parentUuid"),
                root.Str("type"),
                root.Str("timestamp"),
                isRoot ? Title(root, line, sessionId) : null,
                isRoot && EndsTurn(root)
            );
        } catch (JsonException) {
            return LineSignals.None;
        }
    }

    static bool EndsTurn(JsonElement root) =>
        root.Str("type") == "system" && root.Str("subtype") == "turn_duration" && root.Bool("isSidechain") != true;

    static TitleSighting? Title(JsonElement root, string line, string sessionId) {
        if (!ClaudeTitleLine.CarriesTitle(line, sessionId, out _)) return null;

        var (field, kind, legacy) = root.Str("type") switch {
            "ai-title"     => ("aiTitle", "auto", false),
            "custom-title" => ("customTitle", "rename", false),
            "summary"      => ("summary", "auto", true),
            _              => (null, "auto", false),
        };

        return field is not null && Normalize(root.Str(field)) is { } title ? new TitleSighting(kind, title, null, legacy) : null;
    }

    // Collapses whitespace runs to one space and caps the title at 200 characters, as the server stores it.
    static string? Normalize(string? raw) {
        if (raw is null) return null;

        var sb     = new StringBuilder(Math.Min(raw.Length, MaxTitleLength));
        var lastWs = true;

        foreach (var ch in raw) {
            if (sb.Length >= MaxTitleLength) break;

            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) {
                if (!lastWs) sb.Append(' ');
                lastWs = true;
                continue;
            }

            sb.Append(ch);
            lastWs = false;
        }

        // A cut after a high surrogate would leave it unpaired.
        if (sb.Length >= MaxTitleLength && char.IsHighSurrogate(sb[^1])) sb.Length--;

        var title = sb.ToString().TrimEnd();

        return title.Length == 0 ? null : title;
    }
}
