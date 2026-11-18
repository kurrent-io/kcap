using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>An audience a new grant may name, the relations it may take, and the one a plain share gives.</summary>
public record SharingCandidateDto(
    [property: JsonPropertyName("audience")]         SharingAudienceDto       Audience,
    [property: JsonPropertyName("relations")]        List<SharingRelationDto> Relations,
    [property: JsonPropertyName("default_relation")] string                   DefaultRelation);
