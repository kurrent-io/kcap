using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One open interrupt; `event` is its `InterruptIssued` exactly as the events route serves it.
public sealed record PendingInterruptDto {
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("event")]      public SessionEventDto? Event { get; init; }
}
