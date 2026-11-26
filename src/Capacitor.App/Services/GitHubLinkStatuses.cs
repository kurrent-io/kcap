using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// The state of the GitHub pull requests and issues chat links name. A pull request a listed session
/// already holds is taken from there; anything else is looked up in the background when a chip first
/// shows it, one lookup at a time and only while some chip still shows it when its turn comes, and
/// again only once its answer is older than <see cref="Fresh"/>, so scrolling or re-rendering a long
/// chat costs nothing. A merge is final. A lookup that fails leaves the last state standing and is
/// not retried within the same span.
public sealed class GitHubLinkStatuses : IDisposable {
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    const int Capacity = 1024;

    sealed class Entry {
        public GitHubLinkState? State;
        public long? CheckedAt;
        public bool Looking;
        public int Watchers;
        /// Moves on whenever a session's state lands, so a lookup started before it cannot overwrite it.
        public long Revision;
    }

    readonly Func<GitHubLink, CancellationToken, Task<GitHubLinkState?>> _lookup;
    readonly TimeProvider _time;
    readonly IScheduler _scheduler;
    readonly Lock _lock = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Subject<(string Key, GitHubLinkState State)> _changed = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _cancel = new();
    readonly IDisposable _known;
    Dictionary<(string Session, string Key), GitHubLinkState> _reports = [];
    bool _disposed;

    /// <param name="lookup">Null when the item's state cannot be had; it is never asked about one item twice at once.</param>
    /// <param name="sessionPullRequests">The pull requests of listed sessions, whose state needs no lookup of its own.</param>
    /// <param name="scheduler">Where <see cref="Watch"/> delivers a change.</param>
    public GitHubLinkStatuses(Func<GitHubLink, CancellationToken, Task<GitHubLinkState?>> lookup, TimeProvider time, IScheduler scheduler,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? sessionPullRequests = null) {
        _lookup = lookup;
        _time = time;
        _scheduler = scheduler;
        _known = (sessionPullRequests ?? Observable.Empty<IReadOnlyDictionary<string, RailPullRequestSummary>>())
            .Subscribe(Know);
    }

    /// The link's state now and on every change, the current one delivered at once; asks for a lookup
    /// when what is held is missing or stale. Emits nothing while the state is unknown.
    public IObservable<GitHubLinkState> Watch(GitHubLink link) => Observable.Create<GitHubLinkState>(observer => {
        var key = link.Key;
        var changes = _changed.Where(change => change.Key == key).Select(change => change.State).ObserveOn(_scheduler).Subscribe(observer);
        lock (_lock) {
            if (!_disposed) EntryFor(key).Watchers++;
        }
        if (Current(link) is { } state) observer.OnNext(state);
        Request(link);
        return Disposable.Create(() => {
            changes.Dispose();
            lock (_lock) {
                if (_entries.TryGetValue(key, out var entry) && entry.Watchers > 0) entry.Watchers--;
            }
        });
    });

    public GitHubLinkState? Current(GitHubLink link) {
        lock (_lock) return _entries.TryGetValue(link.Key, out var entry) ? entry.State : null;
    }

    /// The rail republishes every session's summary when any one changes, so a report a session made
    /// before comes round again; only a session's changed report is news, and a repeat must not undo a
    /// lookup made since. A repeat that agrees with what is held keeps it fresh.
    void Know(IReadOnlyDictionary<string, RailPullRequestSummary> summaries) {
        lock (_lock) {
            if (_disposed) return;
            var reports = new Dictionary<(string Session, string Key), GitHubLinkState>();
            foreach (var (session, summary) in summaries) {
                foreach (var item in summary.MenuItems) {
                    if (Reported(item) is not var (key, state)) continue;
                    reports[(session, key)] = state;
                    var entry = EntryFor(key);
                    if (_reports.TryGetValue((session, key), out var before) && before == state) {
                        if (entry.State == state) entry.CheckedAt = _time.GetTimestamp();
                        continue;
                    }
                    entry.CheckedAt = _time.GetTimestamp();
                    entry.Revision++;
                    Settle(key, entry, state);
                }
            }
            _reports = reports;
        }
    }

    static (string Key, GitHubLinkState State)? Reported(RailPullRequestMenuItem item) {
        if (item.Subject is not { Provider: "github", Host: "github.com" } subject) return null;
        GitHubLinkState? state = item.Lifecycle switch {
            "Open" => GitHubLinkState.Open,
            "Draft" => GitHubLinkState.Draft,
            "Merged" => GitHubLinkState.Merged,
            "Closed" => GitHubLinkState.Closed,
            _ => null,
        };
        return state is { } known
            ? (new GitHubLink("", GitHubLinkKind.PullRequest, subject.Owner, subject.RepoName, subject.Number).Key, known)
            : null;
    }

    void Request(GitHubLink link) {
        long revision;
        lock (_lock) {
            if (_disposed) return;
            var entry = EntryFor(link.Key);
            if (entry.Looking || entry.State == GitHubLinkState.Merged
                || entry.CheckedAt is { } at && _time.GetElapsedTime(at) < Fresh) return;
            entry.Looking = true;
            revision = entry.Revision;
        }
        _ = LookUpAsync(link, revision);
    }

    async Task LookUpAsync(GitHubLink link, long revision) {
        GitHubLinkState? state = null;
        var asked = false;
        var ct = _cancel.Token;
        try {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (Watched(link.Key)) {
                    asked = true;
                    state = await _lookup(link, ct).ConfigureAwait(false);
                }
            } finally { _gate.Release(); }
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: GitHub link lookup failed: {ex.GetType().Name}");
        }
        bool skipped;
        lock (_lock) {
            if (_disposed || !_entries.TryGetValue(link.Key, out var entry)) return;
            entry.Looking = false;
            // A chip that came back while the skipped lookup was finishing found it still running.
            skipped = !asked && entry.Watchers > 0;
            if (asked && entry.Revision == revision) {
                entry.CheckedAt = _time.GetTimestamp();
                if (state is { } known) Settle(link.Key, entry, known);
            }
        }
        if (skipped) Request(link);
    }

    bool Watched(string key) {
        lock (_lock) return _entries.TryGetValue(key, out var entry) && entry.Watchers > 0;
    }

    // Under the lock, which is what keeps a watcher's current state and its changes in order. A merge
    // cannot be undone, so a session summary older than it is not taken.
    void Settle(string key, Entry entry, GitHubLinkState state) {
        if (entry.State == state || entry.State == GitHubLinkState.Merged) return;
        entry.State = state;
        _changed.OnNext((key, state));
    }

    // Under the lock. An entry a chip shows, or with a lookup in flight, stays, past the capacity if need be.
    Entry EntryFor(string key) {
        if (_entries.TryGetValue(key, out var entry)) return entry;
        if (_entries.Count >= Capacity
            && _entries.Where(pair => pair.Value is { Looking: false, Watchers: 0 }).OrderBy(pair => pair.Value.CheckedAt ?? long.MinValue)
                .Select(pair => pair.Key).FirstOrDefault() is { } oldest) _entries.Remove(oldest);
        return _entries[key] = new Entry();
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _known.Dispose();
        _cancel.Cancel();
        _changed.OnCompleted();
    }
}
