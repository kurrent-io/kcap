using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A Page listed on a work item because it cites one of its sessions the viewer can see.
public sealed record WorkItemPageDto {
    [JsonPropertyName("artefact_id")]    public required string ArtefactId { get; init; }
    [JsonPropertyName("title")]          public string Title { get; init; } = "";
    [JsonPropertyName("latest_version")] public int LatestVersion { get; init; }
    [JsonPropertyName("updated_at")]     public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("url")]            public string Url { get; init; } = "";
}
