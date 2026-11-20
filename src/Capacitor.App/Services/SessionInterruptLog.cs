using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// Folds each session's interrupts from its event stream, reading only the events after the ones
/// already folded. A session read for the first time is folded from its first event: an interrupt
/// issued anywhere in the stream and never resolved is still pending, so no tail window is enough.
/// Given a listing or a summary, a session either one shows ended is answered without reading the
/// stream, and that answer is not kept: a session resumes under the same id. A caller with direct
/// evidence of a pending interrupt passes neither, since both can trail a resume.
/// Reads of one session run one at a time, so a read that queues behind a first scan continues
/// from where the scan stopped. A cursor counts from the stream's first surviving event, so a
/// deleted session and everything after a reconnect, which may have missed a deletion, start over.
/// One log per reconciler: a read joined from elsewhere could predate the ping that asked for it.
public sealed class SessionInterruptLog : ISessionInterruptSource {
    /// From is the server's cursor past the events folded into Interrupts.
    sealed record Folded(long From, InterruptReconciliation Interrupts);

    static readonly InterruptReconciliation Ended = new([], true);
    internal const int Capacity = 64;

    readonly SessionEventsReader _readEvents;
    readonly Func<string, CancellationToken, Task<SummaryFetch>>? _readSummary;
    readonly Func<string, bool?>? _listedEnded;
    /// Least recently used first; a session evicted past Capacity is folded again from its first event.
    readonly OrderedDictionary<string, Folded> _folded = new(StringComparer.Ordinal);
    readonly SessionGates _gates = new();
    readonly IDisposable _subscriptions;
    readonly Lock _lock = new();
    long _generation;

    public SessionInterruptLog(
            SessionEventsReader readEvents, IServerLane? lane = null,
            Func<string, CancellationToken, Task<SummaryFetch>>? readSummary = null, Func<string, bool?>? listedEnded = null) {
        _readEvents = readEvents;
        _readSummary = readSummary;
        _listedEnded = listedEnded;
        _subscriptions = lane is null ? Disposable.Empty : new CompositeDisposable(
            lane.SessionPushes.Where(p => p.Kind == SessionPushKind.Deleted).Subscribe(p => Forget(p.SessionId)),
            lane.Status.Select(s => s.State == ServerLaneState.Connected).DistinctUntilChanged().Where(up => up).Skip(1).Subscribe(_ => Forget()));
    }

    public async Task<SessionInterruptsFetch> ReadAsync(string sessionId, CancellationToken ct) {
        using var _ = await _gates.EnterAsync(sessionId).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Folded? known;
        long generation;
        lock (_lock) {
            known = _folded.GetValueOrDefault(sessionId);
            generation = _generation;
        }
        var listed = _listedEnded?.Invoke(sessionId);
        if (listed is true
            || (listed is null && known is null && _readSummary is { } readSummary
                && await readSummary(sessionId, ct).ConfigureAwait(false) is { Outcome: ServerReadOutcome.Ok, Session.EndedAt: not null }))
            return new(Ended);
        var folded = known ?? new Folded(0, new([], false));
        while (true) {
            var page = await _readEvents(sessionId, folded.From, ct).ConfigureAwait(false);
            if (page.NotFound) {
                lock (_lock) if (generation == _generation) _folded.Remove(sessionId);
                return new(null, NotFound: true);
            }
            if (page.Page is not { } body) {
                if (!ReferenceEquals(folded, known) && folded.From > 0) Commit(sessionId, folded, generation);
                return new(null);
            }
            var events = body.Events ?? [];
            // The server counts events it then withholds from this viewer, so its cursor is the one
            // to follow, past an empty page too; at the tail, re-reading a withheld one folds nothing new.
            var from = folded.From;
            var next = body.NextFrom is { } n && n > from ? n : from + events.Length;
            folded = new(next, InterruptReconciliation.Fold(folded.Interrupts, events));
            if (body.NextFrom is null || next <= from) break;
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

    void Forget(string sessionId) {
        lock (_lock) {
            _generation++;
            _folded.Remove(sessionId);
        }
    }

    void Commit(string sessionId, Folded folded, long generation) {
        lock (_lock) {
            if (generation != _generation) return;
            _folded.Remove(sessionId);
            _folded.Add(sessionId, folded);
            while (_folded.Count > Capacity) _folded.RemoveAt(0);
        }
    }

    public void Dispose() => _subscriptions.Dispose();
}
