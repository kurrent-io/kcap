using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A part of a work item, named just enough to label a session in it.
public sealed record WorkItemPartRefDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
}
