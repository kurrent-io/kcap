using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// A turn's credits and end-of-turn context fill, restated once Kiro's sidecar records them after the
/// turn's reply was already sent. Persisted as `KiroUsageBackfilled`; Kiro reports no tokens to carry.
/// Not named after the server's `KiroUsageBackfilledEvent`: its Kiro normalizer imports both namespaces.
public sealed record KiroUsageBackfill {
    [JsonPropertyName("credits")]
    public double Credits { get; init; }

    [JsonPropertyName("context_usage_percentage")]
    public double? ContextUsagePercentage { get; init; }
}
