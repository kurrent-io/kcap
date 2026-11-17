using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The body of <c>GET /api/work-items?project_id=</c>: the project's open work items, newest first.
public sealed record ProjectWorkItemsResponseDto {
    [JsonPropertyName("work_items")] public List<ProjectWorkItemDto> WorkItems { get; init; } = [];
}
