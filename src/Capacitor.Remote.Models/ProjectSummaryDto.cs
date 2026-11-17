using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One row of <c>GET /api/projects</c>, the fields the desktop app reads.
public sealed record ProjectSummaryDto {
    [JsonPropertyName("project_id")]        public required string ProjectId { get; init; }
    [JsonPropertyName("name")]              public required string Name { get; init; }
    /// "owner", "member" or "none".
    [JsonPropertyName("viewer_membership")] public required string ViewerMembership { get; init; }
    [JsonPropertyName("repo_hashes")]       public List<string> RepoHashes { get; init; } = [];
}
