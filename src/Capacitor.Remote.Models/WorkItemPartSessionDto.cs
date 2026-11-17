using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemPartSessionDto {
    [JsonPropertyName("session_id")]       public required string SessionId { get; init; }
    [JsonPropertyName("title")]            public string? Title { get; init; }
    [JsonPropertyName("last_activity_at")] public DateTimeOffset? LastActivityAt { get; init; }
}
