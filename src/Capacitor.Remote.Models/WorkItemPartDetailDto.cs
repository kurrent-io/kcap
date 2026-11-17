using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemPartDetailDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
    [JsonPropertyName("links")]        public List<WorkItemLinkDto> Links { get; init; } = [];
    [JsonPropertyName("waits_on")]     public List<WorkItemRefDto> WaitsOn { get; init; } = [];
    [JsonPropertyName("status")]       public string Status { get; init; } = "";
    [JsonPropertyName("session_ids")]  public List<string> SessionIds { get; init; } = [];
}
