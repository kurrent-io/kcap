using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A session in a work item, with the part it sits in, or null when it sits on the work item itself, and when it last
/// moved, absent from a server older than the field.
public sealed record WorkItemSessionRefDto {
    [JsonPropertyName("session_id")]       public required string SessionId { get; init; }
    [JsonPropertyName("part_id")]          public string? PartId { get; init; }
    [JsonPropertyName("owner")]            public RepoSessionOwnerDto? Owner { get; init; }
    [JsonPropertyName("last_activity_at")] public DateTimeOffset? LastActivityAt { get; init; }
}
