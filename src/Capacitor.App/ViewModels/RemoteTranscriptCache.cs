using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The projected lines of the remote sessions opened most recently, so re-opening one reads only what
/// followed and shows it with that one small read. It holds data, never a view model, so a closed tab's rows are not kept alive. A
/// session whose access is refused is forgotten, and every session goes when the signed-in account changes.
public sealed class RemoteTranscriptCache : IDisposable {
    public const int Capacity = 5;
    /// Lines kept per session; the oldest beyond it are paged in again if the reader scrolls back to them.
    internal const int MaxLines = 2000;

    readonly Lock _lock = new();
    /// Most recently opened first.
    readonly LinkedList<(string SessionId, RemoteTranscriptSnapshot Snapshot)> _entries = new();
    readonly IDisposable _subscriptions;
    int _generation;

    public RemoteTranscriptCache(IObservable<string?> signedInSubject, IObservable<(string SessionId, SessionAccessState State)> access) {
        _subscriptions = new CompositeDisposable(
            signedInSubject.Subscribe(_ => Clear()),
            access.Where(change => change.State == SessionAccessState.Denied).Subscribe(change => Drop(change.SessionId)));
    }

    internal int Count { get { lock (_lock) return _entries.Count; } }

    /// Advances with every change of account: a snapshot read under an earlier one is never kept.
    internal int Generation { get { lock (_lock) return _generation; } }

    internal bool Contains(string sessionId) { lock (_lock) return Find(sessionId) is not null; }

    /// Hands the session's snapshot to the one feed that opens it.
    internal RemoteTranscriptSnapshot? Take(string sessionId) {
        lock (_lock) {
            if (Find(sessionId) is not { } node) return null;
            _entries.Remove(node);
            return node.Value.Snapshot;
        }
    }

    internal void Put(string sessionId, RemoteTranscriptSnapshot snapshot, int generation) {
        lock (_lock) {
            if (generation != _generation) return;
            if (Find(sessionId) is { } stale) _entries.Remove(stale);
            _entries.AddFirst((sessionId, snapshot));
            while (_entries.Count > Capacity) _entries.RemoveLast();
        }
    }

    public void Drop(string sessionId) {
        lock (_lock) if (Find(sessionId) is { } node) _entries.Remove(node);
    }

    void Clear() {
        lock (_lock) {
            _entries.Clear();
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
