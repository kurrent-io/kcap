namespace Capacitor.Models.Transcripts.Harness.Pi;

/// Pi's tree as far as this stream has read it: every entry's parent link, control entries that produced
/// nothing included, and which entries produced an event. It spans the session, so nothing is cleared at a
/// batch boundary.
public sealed class PiTranscriptContext : TranscriptContext {
    readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);
    readonly HashSet<string>             _emitted = new(StringComparer.Ordinal);

    internal PiTranscriptContext(string sessionId) => SessionId = sessionId;

    internal string SessionId { get; }

    /// An entry the stream held before this context existed; without it an entry whose parent was read by an
    /// earlier context resolves no render parent. Stored events give each emitting entry and its parent, with
    /// <paramref name="emitted"/> set, but no control entry's link: an entry whose parent is a control entry
    /// resolves none unless the caller seeds that link too.
    public void Seed(string entryId, string? parentId, bool emitted) {
        _parents[entryId] = parentId;
        if (emitted) _emitted.Add(entryId);
    }

    internal void Record(string entryId, string? parentId) => _parents[entryId] = parentId;

    internal void MarkEmitted(string entryId) => _emitted.Add(entryId);

    internal string? NearestEmittedAncestor(string? parentId) {
        var current = parentId;

        // Bounded because a malformed file can link entries into a cycle.
        for (var steps = 0; current is not null && steps < 100_000; steps++) {
            if (_emitted.Contains(current)) return current;

            current = _parents.GetValueOrDefault(current);
        }

        return null;
    }
}
