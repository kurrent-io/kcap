using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The caller's own pass controls: whom they muted, and whether their coding agent hears about passes.</summary>
public sealed record PassControlsDto {
    [JsonPropertyName("muted_senders")]   public IReadOnlyList<PassMutedSenderDto> MutedSenders  { get; init; } = [];
    [JsonPropertyName("agent_opted_out")] public bool                              AgentOptedOut { get; init; }
}
