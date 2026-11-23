using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A session's unresolved interrupts as of the stream position the server folded to. `next_from` is the
/// events route's `from` count to continue folding from; `last_event_number` is a revision, not that count.
public sealed record PendingInterruptsPage {
    [JsonPropertyName("last_event_number")] public long LastEventNumber { get; init; } = -1;
    [JsonPropertyName("next_from")]         public int NextFrom { get; init; }
    [JsonPropertyName("ended")]             public bool Ended { get; init; }
    [JsonPropertyName("interrupts")]        public PendingInterruptDto[]? Interrupts { get; init; }
}
