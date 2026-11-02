using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A file held with a message, without its bytes.
public sealed record ListedHeldAttachmentDto {
    [JsonPropertyName("attachment_id")] public string AttachmentId { get; init; } = "";
    [JsonPropertyName("file_name")]     public string FileName { get; init; } = "";
    [JsonPropertyName("content_type")]  public string ContentType { get; init; } = "";
    [JsonPropertyName("size_bytes")]    public long SizeBytes { get; init; }
}
