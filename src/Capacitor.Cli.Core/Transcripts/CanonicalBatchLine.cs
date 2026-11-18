using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>One covered source line and what the server reads from a raw line besides its events: the record's ids,
/// its type and its timestamp, each as written.</summary>
public sealed record CanonicalBatchLine(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("uuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uuid,
        [property: JsonPropertyName("parent_uuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentUuid,
        [property: JsonPropertyName("record_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecordType,
        [property: JsonPropertyName("timestamp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Timestamp
    );
