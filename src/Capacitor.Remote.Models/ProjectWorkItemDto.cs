using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One open work item of a project, the fields the desktop app reads. The part counts are the same for every
/// viewer; the sessions are only those the viewer can see.
public sealed record ProjectWorkItemDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
    [JsonPropertyName("status")]       public string? Status { get; init; }
    [JsonPropertyName("parts_done")]   public int PartsDone { get; init; }
    [JsonPropertyName("parts_total")]  public int PartsTotal { get; init; }
    [JsonPropertyName("sessions")]     public List<WorkItemSessionRefDto> Sessions { get; init; } = [];
    [JsonPropertyName("parts")]        public List<WorkItemRefDto> Parts { get; init; } = [];
}
