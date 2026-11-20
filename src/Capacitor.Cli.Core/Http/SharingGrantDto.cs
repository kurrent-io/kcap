using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A grant with what the caller may do to it: the <see cref="Relations"/> it may be moved to and whether it is
/// <see cref="Removable"/>, empty and false for a caller who may not manage.</summary>
public record SharingGrantDto(
    [property: JsonPropertyName("audience")]  SharingAudienceDto       Audience,
    [property: JsonPropertyName("relation")]  SharingRelationDto       Relation,
    [property: JsonPropertyName("relations")] List<SharingRelationDto> Relations,
    [property: JsonPropertyName("removable")] bool                     Removable);
