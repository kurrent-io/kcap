namespace Capacitor.Models.Transcripts;

/// Per-stream state a projection keeps between lines. The caller owns one per stream and calls
/// BeginBatch at every batch boundary; what a vendor clears there is the vendor's to define.
public abstract class TranscriptContext {
    public virtual void BeginBatch() { }

    /// The caller stored an event this context's projection returned, or found it already stored.
    /// Only a vendor whose output depends on what is stored keeps it; the caller reports every
    /// event, for every vendor, never only the ones it expects to matter.
    public virtual void MarkPersisted(Guid eventId) { }
}
