using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>An attachment's bytes, and the event and block index its id derives from, so the server can derive the id
/// from the session it established and refuse one that does not match.</summary>
public sealed record CanonicalBatchAttachment(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("message_id")] Guid MessageId,
        [property: JsonPropertyName("block_index")] int BlockIndex,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("content_type")] string ContentType,
        [property: JsonPropertyName("data")] byte[] Data
    );
