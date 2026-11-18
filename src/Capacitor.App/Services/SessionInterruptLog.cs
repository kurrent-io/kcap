namespace Capacitor.App.Services;

/// Folds each session's interrupts from its event stream, reading only the events after the ones
/// already folded. A session read for the first time is folded from its first event: an interrupt
/// issued anywhere in the stream and never resolved is still pending, so no tail window is enough.
/// Only an ended session skips that, on its summary's word, since an end clears every interrupt.
/// One log per reconciler: a read joined from elsewhere could predate the ping that asked for it.
public sealed class SessionInterruptLog(SessionEventsReader readEvents, Func<string, CancellationToken, Task<SummaryFetch>> readSummary)
        : ISessionInterruptSource {
    /// From is the count of events folded into Interrupts.
    sealed record Folded(long From, InterruptReconciliation Interrupts);

    static readonly Folded Empty = new(0, new([], false));

    readonly Dictionary<string, Folded> _folded = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    long _generation;

    public async Task<SessionInterruptsFetch> ReadAsync(string sessionId, CancellationToken ct) {
        Folded? known;
        long generation;
        lock (_lock) {
            known = _folded.GetValueOrDefault(sessionId);
            generation = _generation;
        }
        if (known is { Interrupts.Ended: true }) return new(known.Interrupts);
        if (known is null && await readSummary(sessionId, ct).ConfigureAwait(false) is { Outcome: ServerReadOutcome.Ok, Session.EndedAt: not null }) {
            var ended = new Folded(0, new([], true));
            Commit(sessionId, ended, generation);
            return new(ended.Interrupts);
        }
        var folded = known ?? Empty;
        while (true) {
            var page = await readEvents(sessionId, folded.From, ct).ConfigureAwait(false);
            if (page.NotFound) {
                lock (_lock) if (generation == _generation) _folded.Remove(sessionId);
                return new(null, NotFound: true);
            }
            if (page.Page is not { } body) {
                if (!ReferenceEquals(folded, known ?? Empty)) Commit(sessionId, folded, generation);
                return new(null);
            }
            var events = body.Events ?? [];
            var next = body.NextFrom is { } n && n > folded.From ? n : folded.From + events.Length;
            folded = new(next, InterruptReconciliation.Fold(folded.Interrupts, events));
            if (body.NextFrom is null || events.Length == 0 || folded.Interrupts.Ended) break;
        }
        Commit(sessionId, folded, generation);
        return new(folded.Interrupts);
    }

    public void Forget() {
        lock (_lock) {
            _generation++;
            _folded.Clear();
        }
    }

    /// Two reads of one session can overlap; the one that folded further holds everything the other saw.
    void Commit(string sessionId, Folded folded, long generation) {
        lock (_lock) {
            if (generation != _generation) return;
            if (_folded.TryGetValue(sessionId, out var current)
                && (current.Interrupts.Ended || !folded.Interrupts.Ended && current.From > folded.From)) return;
            _folded[sessionId] = folded;
        }
    }
}
