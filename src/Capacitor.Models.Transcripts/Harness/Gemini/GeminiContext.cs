namespace Capacitor.Models.Transcripts.Harness.Gemini;

/// The ids the caller has stored, for the stream's life. Gemini re-appends a growing message under
/// its own id, so a later snapshot repeats parts an earlier one emitted; these are what it leaves
/// out. Only the caller adds to it, after a store: an event that was returned but never stored is
/// returned again when its line is retried.
public sealed class GeminiContext : TranscriptContext {
    readonly HashSet<Guid> _persisted = [];

    public override void MarkPersisted(Guid eventId) => _persisted.Add(eventId);

    internal bool IsPersisted(Guid eventId) => _persisted.Contains(eventId);
}
