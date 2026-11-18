using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// One generation's tokens, persisted as `AntigravityUsageBackfilled`. Antigravity keeps them in its conversation
/// database, not the transcript, and has no per-message counts, so these are the whole amounts, reasoning folded
/// into output.
public sealed record AntigravityUsageBackfill {
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }
}
