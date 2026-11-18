using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A relation a grant may take. Relations belong to the object, so one this binary does not know is shown by its label.</summary>
public record SharingRelationDto(
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("label")]    string Label);
