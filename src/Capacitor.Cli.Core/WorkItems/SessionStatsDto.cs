using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// The usage figures of a session summary's <c>stats</c>. <see cref="ContextWindow"/> and
/// <see cref="EstimatedCostUsd"/> are priced by the server and absent from an older one.
public sealed record SessionStatsDto {
    List<SessionModelTokensDto> _tokens = [];

    [JsonPropertyName("token_usage_by_model")]  public List<SessionModelTokensDto> TokenUsageByModel   { get => _tokens; init => _tokens = value ?? []; }
    [JsonPropertyName("peak_context_tokens")]   public long                        PeakContextTokens   { get; init; }
    [JsonPropertyName("peak_context_window")]   public long?                       PeakContextWindow   { get; init; }
    [JsonPropertyName("context_usage_percent")] public double?                     ContextUsagePercent { get; init; }
    [JsonPropertyName("context_window")]        public long?                       ContextWindow       { get; init; }
    [JsonPropertyName("current_context_tokens")]    public long?   CurrentContextTokens     { get; init; }
    [JsonPropertyName("current_context_window")]    public long?   CurrentContextWindow     { get; init; }
    [JsonPropertyName("current_context_percent")]   public double? CurrentContextPercent    { get; init; }
    [JsonPropertyName("awaiting_context_reading")]  public bool    AwaitingContextReading   { get; init; }
    [JsonPropertyName("estimated_cost_usd")]    public double?                     EstimatedCostUsd    { get; init; }
}
