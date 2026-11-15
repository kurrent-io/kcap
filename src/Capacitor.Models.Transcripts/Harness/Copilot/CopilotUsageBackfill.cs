using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// Tokens one model used that no other event counts: one model call from a <c>session.usage_record</c>, or a run's
/// per-model totals from a <c>session.shutdown</c>, less what the transcript already reported, with the model's
/// billing. Persisted as <c>CopilotUsageBackfilled</c> under the server's JSON names.
public sealed record CopilotUsageBackfill {
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// Net of cache reads and writes, which Copilot's input count includes.
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    /// Zero when the run's assistant messages already reported the output.
    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public long? ReasoningTokens { get; init; }

    /// The model the session itself runs on, set only from the parent agent's own report: a subagent on another
    /// model never names the session.
    [JsonPropertyName("session_model")]
    public string? SessionModel { get; init; }

    [JsonPropertyName("request_count")]
    public long? RequestCount { get; init; }

    /// Copilot's premium-request units.
    [JsonPropertyName("request_cost")]
    public double? RequestCost { get; init; }

    /// Copilot's billing unit, 1e9 to an AI credit.
    [JsonPropertyName("nano_aiu")]
    public long? NanoAiu { get; init; }
}
