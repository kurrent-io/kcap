using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkFacetsDto(
    [property: JsonPropertyName("others_waiting")]   int    OthersWaiting,
    [property: JsonPropertyName("tracker_priority")] int?   TrackerPriority,
    [property: JsonPropertyName("is_bug")]           bool   IsBug,
    [property: JsonPropertyName("sighting_count")]   int    SightingCount,
    [property: JsonPropertyName("staleness_days")]   double StalenessDays);
