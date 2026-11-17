using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The work item kcap suggests for a session in no work item, with the reason it gives.
public sealed record WorkItemSuggestionDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
    [JsonPropertyName("reason")]       public string? Reason { get; init; }
}
