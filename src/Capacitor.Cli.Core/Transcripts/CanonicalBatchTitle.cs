using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>A harness title a covered line carries. <paramref name="Kind"/> is <c>auto</c> or <c>rename</c>;
/// <paramref name="OwnTime"/> is set only when the line records when the title changed.</summary>
public sealed record CanonicalBatchTitle(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("own_time"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OwnTime,
        [property: JsonPropertyName("legacy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Legacy,
        [property: JsonPropertyName("line")] int Line
    );
