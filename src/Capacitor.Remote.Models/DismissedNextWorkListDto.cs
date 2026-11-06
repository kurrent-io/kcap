using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record DismissedNextWorkListDto(
    [property: JsonPropertyName("items")]     List<DismissedNextWorkDto>? Items,
    [property: JsonPropertyName("truncated")] bool                        Truncated);
