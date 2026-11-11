using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts;

/// The person cancelled the prompt MessageId before the agent answered it, so it never reached the
/// model; persisted as `UserMessageRetracted`, immediately before the prompt that replaced it, which
/// keeps both inside one turn. The prompt itself stays as it was stored.
public sealed record UserMessageRetractedEvent {
    [JsonPropertyName("message_id")]
    public required Guid MessageId { get; init; }
}
