using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>A capture-loss line as the recorder wrote it, for the server's own parser.</summary>
public sealed record CanonicalBatchCaptureLoss(
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("raw")] string Raw
    );
