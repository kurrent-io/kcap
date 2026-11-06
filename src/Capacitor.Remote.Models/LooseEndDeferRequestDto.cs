using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record LooseEndDeferRequestDto(
    [property: JsonPropertyName("loose_end_id")] string         LooseEndId,
    [property: JsonPropertyName("until")]        DateTimeOffset Until);
