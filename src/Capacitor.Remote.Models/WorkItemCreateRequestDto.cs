using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemCreateRequestDto(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("title")]      string Title);
