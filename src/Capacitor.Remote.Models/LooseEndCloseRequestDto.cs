using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record LooseEndCloseRequestDto([property: JsonPropertyName("loose_end_id")] string LooseEndId);
// CI selection probe, to be discarded.
