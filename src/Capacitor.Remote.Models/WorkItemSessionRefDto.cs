using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A session in a work item, with the part it sits in, or null when it sits on the work item itself.
public sealed record WorkItemSessionRefDto {
    [JsonPropertyName("session_id")] public required string SessionId { get; init; }
    [JsonPropertyName("part_id")]    public string? PartId { get; init; }
    [JsonPropertyName("owner")]      public RepoSessionOwnerDto? Owner { get; init; }
}
