using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <see cref="WaitsOnId"/> is another part of the same work item.
public sealed record WorkItemWaitRequestDto([property: JsonPropertyName("waits_on_id")] string WaitsOnId);
