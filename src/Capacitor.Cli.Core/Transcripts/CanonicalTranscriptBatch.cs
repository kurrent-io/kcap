using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Transcript lines the client projected to canonical events, for the server's canonical ingress. The JSON
/// names are the contract the server reads.</summary>
public sealed record CanonicalTranscriptBatch {
    /// The envelope's version: a server that does not know it refuses the batch. New event types, payload fields or
    /// metadata keys do not change it.
    [JsonPropertyName("format")]
    public int Format { get; init; } = 1;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    [JsonPropertyName("vendor")]
    public required string Vendor { get; init; }

    /// Every line the batch covers, in order, a line that produced nothing included: the server's frontier moves past
    /// all of them.
    [JsonPropertyName("lines")]
    public required CanonicalBatchLine[] Lines { get; init; }

    [JsonPropertyName("events")]
    public required CanonicalBatchEvent[] Events { get; init; }

    [JsonPropertyName("titles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalBatchTitle[]? Titles { get; init; }

    /// The highest covered line that ends a turn.
    [JsonPropertyName("turn_end_line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TurnEndLine { get; init; }

    [JsonPropertyName("capture_loss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalBatchCaptureLoss[]? CaptureLoss { get; init; }

    [JsonPropertyName("repository")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryPayload? Repository { get; init; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Strict { get; init; }

    /// Non-null, even empty, and the server reads no commits from the events' shell commands.
    [JsonPropertyName("observed_commits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservedCommit[]? ObservedCommits { get; init; }

    [JsonPropertyName("more_follows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MoreFollows { get; init; }
}
