using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The body of <c>GET /api/work-items/{id}/parts</c>. Statuses are the same for every viewer; session ids are only
/// those the viewer can see, and loose ends are the viewer's own.
public sealed record WorkItemPartsDto {
    [JsonPropertyName("work_item")]   public required WorkItemRefDto WorkItem { get; init; }
    [JsonPropertyName("links")]       public List<WorkItemLinkDto> Links { get; init; } = [];
    [JsonPropertyName("parts")]       public List<WorkItemPartDetailDto> Parts { get; init; } = [];
    [JsonPropertyName("goal")]        public string? Goal { get; init; }
    [JsonPropertyName("status")]      public string Status { get; init; } = "";
    [JsonPropertyName("session_ids")] public List<string> SessionIds { get; init; } = [];
    [JsonPropertyName("loose_ends")]  public List<WorkItemLooseEndDto> LooseEnds { get; init; } = [];
    /// Titles for <see cref="SessionIds"/>; an older server sends none.
    [JsonPropertyName("sessions")]    public List<WorkItemPartSessionDto> Sessions { get; init; } = [];
}
