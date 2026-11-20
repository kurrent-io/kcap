using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A grant as a page listing shows it, without the caller's rights over it.</summary>
public record SharedGrantDto(
    [property: JsonPropertyName("audience")] SharingAudienceDto Audience,
    [property: JsonPropertyName("relation")] SharingRelationDto Relation);
