using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A null status shows the worked-out one again.
public sealed record WorkItemStatusRequestDto([property: JsonPropertyName("status")] string? Status);
