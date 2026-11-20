using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One page of a session's or a subagent's events. The server's `from` counts events from the oldest one the
/// stream still holds, which is not an event number once older events have aged out; a next start is set
/// while more events follow this page.
public sealed record StreamEventsPage {
    [JsonPropertyName("last_event_number")] public long LastEventNumber { get; init; } = -1;
    [JsonPropertyName("events")]            public SessionEventDto[]? Events { get; init; }
    [JsonPropertyName("next_from")]         public int? NextFrom { get; init; }
}
