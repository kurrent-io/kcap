using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Every slice of the work item the viewer can see, first to last.
public sealed record WorkItemPartOrderRequestDto([property: JsonPropertyName("part_ids")] IReadOnlyList<string> PartIds);
