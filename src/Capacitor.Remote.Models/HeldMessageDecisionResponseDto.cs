using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Where a decided message now stands: dispatched, rejected, withdrawn, or discarded when the agent had stopped or the
/// sender could no longer reach it.
public sealed record HeldMessageDecisionResponseDto {
    [JsonPropertyName("state")] public string State { get; init; } = "";
}
