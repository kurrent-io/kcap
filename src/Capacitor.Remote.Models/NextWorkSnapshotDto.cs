using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The body of <c>GET /api/next-work/snapshot</c>: the engine's kept and disconfirmed candidates for the
/// viewer, the two flags that say the list may be missing items, and display names for the repositories they name.</summary>
public sealed record NextWorkSnapshotDto(
    [property: JsonPropertyName("as_of")]      DateTimeOffset?                       AsOf,
    [property: JsonPropertyName("partial")]    bool                                  Partial,
    [property: JsonPropertyName("incomplete")] bool                                  Incomplete,
    [property: JsonPropertyName("repos")]      Dictionary<string, string>?           Repos,
    [property: JsonPropertyName("candidates")] List<NextWorkCandidateDto>?           Candidates);
