using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The work item a listed session sits in. The status and part counts are the same for every viewer; Part is the
/// part of it the session sits in, or null when it sits on the work item itself.
public sealed record RepoSessionWorkItemDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title { get; init; }
    [JsonPropertyName("status")]       public string? Status { get; init; }
    [JsonPropertyName("parts_done")]   public int PartsDone { get; init; }
    [JsonPropertyName("parts_total")]  public int PartsTotal { get; init; }
    [JsonPropertyName("part")]         public WorkItemRefDto? Part { get; init; }
    /// Null from a server that predates it.
    [JsonPropertyName("created_at")]   public DateTimeOffset? CreatedAt { get; init; }
}
