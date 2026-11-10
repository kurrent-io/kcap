using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Kiro;

/// <summary>
/// Minimal reader for Kiro's per-session <c>{id}.json</c> metadata sibling — the
/// few fields import needs (cwd, title, model, timestamps). A parse failure must
/// never break discovery (returns null / partial data).
/// </summary>
public sealed record KiroSessionMeta(string? Cwd, string? Title, string? Model, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt) {
    public static KiroSessionMeta? TryRead(string jsonPath) {
        try {
            if (!File.Exists(jsonPath)) return null;
            if (JsonNode.Parse(File.ReadAllTextShared(jsonPath)) is not JsonObject root) return null;

            return new KiroSessionMeta(
                Cwd:       root["cwd"]?.GetValue<string>(),
                Title:     root["title"]?.GetValue<string>(),
                Model:     root["session_state"]?["rts_model_state"]?["model_info"]?["model_id"]?.GetValue<string>(),
                CreatedAt: ParseTimestamp(root["created_at"]?.GetValue<string>()),
                UpdatedAt: ParseTimestamp(root["updated_at"]?.GetValue<string>()));
        } catch {
            return null;
        }
    }

    static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;
}
