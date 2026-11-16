using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>One canonical event and the named fields the server builds its stored metadata from. There is no
/// free-form metadata: anything else the server stores beside the event, it sets itself.</summary>
public sealed record CanonicalBatchEvent {
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// The payload in the JSON form the server stores.
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    [JsonPropertyName("event_id")]
    public required Guid EventId { get; init; }

    /// The source line the event is stored under.
    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// The record's own timestamp, as written.
    [JsonPropertyName("record_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordTimestamp { get; init; }

    [JsonPropertyName("caused_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausedBy { get; init; }

    /// The schema's token usage, in the same JSON form as a payload.
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Usage { get; init; }

    [JsonPropertyName("usage_is_echo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UsageIsEcho { get; init; }

    [JsonPropertyName("cache_creation_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheCreationTokens { get; init; }

    /// Metadata keys the vendor's stored events carry beside the common ones, each a string, a number or a boolean.
    [JsonPropertyName("vendor_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? VendorMetadata { get; init; }

    [JsonPropertyName("attachments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalBatchAttachment[]? Attachments { get; init; }

    /// Reserved for a dispatch stamp; a format 1 server ignores it.
    [JsonPropertyName("stamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Stamp { get; init; }
}
