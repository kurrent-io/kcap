using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The projected lines of the remote sessions closed most recently, so re-opening one reads only what
/// followed and shows it with that one small read. It holds data, never a view model, so a closed tab's rows are not kept alive. A
/// session whose access is refused is forgotten, and every session goes when the signed-in account changes.
public sealed class RemoteTranscriptCache : IDisposable {
    public const int Capacity = 5;
    /// Lines kept per session; the oldest beyond it are paged in again if the reader scrolls back to them.
    internal const int MaxLines = 2000;

    readonly Lock _lock = new();
    /// Most recently closed first: a transcript is kept when its feed closes, so the one read last stays longest.
    readonly LinkedList<(string SessionId, RemoteTranscriptSnapshot Snapshot)> _entries = new();
    readonly CompositeDisposable _subscriptions;
    int _generation;
    /// How many times each session was refused; a feed loaded before the latest refusal cannot keep its lines.
    readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);

    public RemoteTranscriptCache(IObservable<string?> signedInSubject, IObservable<(string SessionId, SessionAccessState State)> access) {
        _subscriptions = new CompositeDisposable(
            signedInSubject.Subscribe(_ => Clear()),
            access.Where(change => change.State == SessionAccessState.Denied).Subscribe(change => Drop(change.SessionId)));
    }

    internal int Count { get { lock (_lock) return _entries.Count; } }

    /// Taken when a feed starts loading and checked when it closes: a change of account or a refusal of the
    /// session in between means its lines are never kept.
    internal (int Generation, int Refusals) Stamp(string sessionId) {
        lock (_lock) return (_generation, _refusals.GetValueOrDefault(sessionId));
    }

    /// Runs publish only while the stamp still holds, under the lock an account change and a refusal take,
    /// so neither can land between the check and what it guards; false when the stamp no longer holds.
    internal bool WhileCurrent(string sessionId, (int Generation, int Refusals) stamp, Action publish) {
        lock (_lock) {
            if (stamp != (_generation, _refusals.GetValueOrDefault(sessionId))) return false;
            publish();
            return true;
        }
    }

    internal bool Contains(string sessionId) { lock (_lock) return Find(sessionId) is not null; }

    /// Hands the session's snapshot to the one feed that opens it.
    internal RemoteTranscriptSnapshot? Take(string sessionId) {
        lock (_lock) {
            if (Find(sessionId) is not { } node) return null;
            _entries.Remove(node);
            return node.Value.Snapshot;
        }
    }

    internal void Put(string sessionId, RemoteTranscriptSnapshot snapshot, (int Generation, int Refusals) stamp) {
        lock (_lock) {
            if (stamp != (_generation, _refusals.GetValueOrDefault(sessionId))) return;
            if (Find(sessionId) is { } stale) _entries.Remove(stale);
            _entries.AddFirst((sessionId, snapshot));
            while (_entries.Count > Capacity) _entries.RemoveLast();
        }
    }

    public void Drop(string sessionId) {
        lock (_lock) {
            _refusals[sessionId] = _refusals.GetValueOrDefault(sessionId) + 1;
            if (Find(sessionId) is { } node) _entries.Remove(node);
        }
    }

    void Clear() {
        lock (_lock) {
            _entries.Clear();
            _refusals.Clear();
            _generation++;
        }
    }

    LinkedListNode<(string SessionId, RemoteTranscriptSnapshot Snapshot)>? Find(string sessionId) {
        for (var node = _entries.First; node is not null; node = node.Next)
            if (node.Value.SessionId == sessionId) return node;
        return null;
    }

    public void Dispose() {
        _subscriptions.Dispose();
        Clear();
    }
}
