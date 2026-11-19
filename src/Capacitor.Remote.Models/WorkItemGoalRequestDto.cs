using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A null goal clears it.
public sealed record WorkItemGoalRequestDto([property: JsonPropertyName("goal")] string? Goal);
