using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A colleague as the server's people picker shows them.</summary>
public record UserCandidateDto(
    [property: JsonPropertyName("id")]           string  Id,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("avatar_url")]   string? AvatarUrl);
