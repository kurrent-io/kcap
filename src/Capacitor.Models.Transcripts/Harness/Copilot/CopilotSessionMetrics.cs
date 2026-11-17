using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// A run's billing totals and code changes from <c>session.shutdown</c> or <c>session.usage_checkpoint</c>, or one VS
/// Code Local request's credits so far. Persisted as <c>CopilotSessionMetrics</c> under the server's JSON names. A null
/// total is an axis the line did not report, never a zero: the server's fold skips it.
public sealed record CopilotSessionMetrics {
    [JsonPropertyName("total_nano_aiu")]
    public long? TotalNanoAiu { get; init; }

    /// Fractional on models billed below one request.
    [JsonPropertyName("total_premium_requests")]
    public double? TotalPremiumRequests { get; init; }

    /// Set only on a VS Code Local request's credits, which report no run total.
    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    /// True when the line's accounting watermarks show the totals are session-wide, so a later run's include the
    /// earlier runs'; absent when the harness restarts its counters per run.
    [JsonPropertyName("cumulative_across_runs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CumulativeAcrossRuns { get; init; }

    /// The envelope id of the run's <c>session.start</c> or <c>session.resume</c>; null when the stream's context
    /// never saw one.
    [JsonPropertyName("run_marker")]
    public Guid? RunMarker { get; init; }

    [JsonPropertyName("lines_added")]
    public int? LinesAdded { get; init; }

    [JsonPropertyName("lines_removed")]
    public int? LinesRemoved { get; init; }

    [JsonPropertyName("files_modified_count")]
    public int? FilesModifiedCount { get; init; }

    [JsonPropertyName("files_modified")]
    public IReadOnlyList<string>? FilesModified { get; init; }
}
