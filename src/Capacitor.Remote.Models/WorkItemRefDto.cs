using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A work item or part, named just enough to render and follow it.
public sealed record WorkItemRefDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
}
