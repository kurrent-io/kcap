using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkEvidenceDto(
    [property: JsonPropertyName("kind")]         string  Kind,
    [property: JsonPropertyName("source")]       string? Source,
    [property: JsonPropertyName("summary")]      string? Summary,
    [property: JsonPropertyName("excerpt")]      string? Excerpt = null,
    [property: JsonPropertyName("loose_end_id")] string? LooseEndId = null,
    [property: JsonPropertyName("session_id")]   string? SessionId = null);
