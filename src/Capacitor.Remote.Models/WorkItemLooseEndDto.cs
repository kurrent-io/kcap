using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemLooseEndDto {
    [JsonPropertyName("loose_end_id")] public required string LooseEndId { get; init; }
    [JsonPropertyName("text")]         public required string Text { get; init; }
}
