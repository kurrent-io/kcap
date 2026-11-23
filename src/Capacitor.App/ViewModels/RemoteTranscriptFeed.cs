using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Models.Transcripts.UserMessages;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.App.ViewModels;

/// The chat rows of a session on another machine: a seed of the session's last events, then a live
/// tail of its stream from the position the seed ended at, with older events paged in on request.
/// Every access establishment restarts the tail from the last position seen; the seed is fetched
/// only until one lands, so a reconnect resumes rather than replays. Changes wake the pane to drain rows.
/// Facts that span the whole session hold only for what is loaded: an older page that would reclassify a
/// loaded line as a scheduled prompt re-reads everything loaded as a fresh seed.
internal sealed class RemoteTranscriptFeed : IChatTranscriptFeed {
    /// Gaps between tails that ended while access still stood; each further one is the next entry.
    internal static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    /// Events in the seed and in each older page.
    internal const int Window = 300;
    /// The server's cap on one page.
    const int MaxPage = 1000;
    /// Corrections one page may take for events aging out while it is asked for, before its gap is accepted.
    const int MaxRealignments = 3;
    /// Events a session loads without the reader scrolling up. Each older page grows the scroll range
    /// under the reader, so paging in behind the tail settles the scrollbar before it is used.
    internal const int Prefetch = 5000;

    readonly string _sessionId;
    /// Null seeds the whole session.
    readonly int? _window;
    readonly int _prefetch;
    readonly IChatDisplayRules? _rules;
    /// A seed fills its own and it replaces this one only when the seed is taken, so a seed abandoned
    /// half way leaves nothing behind. Tails add to it in stream order, locked on the instance: a
    /// superseded tail can still be projecting the event the next one starts from. Its Declined set is
    /// every loaded line's turned-down check, which an older page is tested against.
    ScheduledPrompts _scheduled = NewScheduled();
    readonly SessionEventsReader _readEvents;
    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Action<string> _log;
    /// Under <see cref="_lock"/>, so a superseded attempt cannot clear what the current one logged.
    readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly List<ProjectedLine> _pending = new();
    readonly IDisposable _access;
    readonly CancellationTokenSource _lifetime = new();
    FeedStatus _pendingStatus = FeedStatus.Ok;
    /// A refusal cannot displace an undrained seed Reset. It repeats on subsequent reads until a
    /// new attempt or seed retires it.
    (FeedStatus Status, string? Failure)? _verdict;
    // A refusal behind a Reset needs another drain, without repeatedly waking for a held verdict.
    bool _verdictRead;
    /// The last event number applied; null until the seed lands.
    long? _position;
    /// How many events of the stream lie before the oldest one loaded, in the server's `from` count.
    long? _earliest;
    /// The event number of the oldest event loaded. The server's count starts wherever its oldest held
    /// event now is, so this is what an older page is checked against once events age out.
    long? _oldest;
    /// The newest line handed to the pane; a re-read keeps everything after it out of its replayed history.
    long _delivered = -1;
    /// Live lines committed while a re-read is in flight, drained or not: its reply can end before them.
    List<ProjectedLine>? _liveDuringReread;
    /// Older lines not yet drained, oldest first.
    readonly List<ProjectedLine> _older = new();
    Task? _olderRun;
    /// Set by a refused older page; asking again would be refused the same way until access is granted anew.
    bool _olderRefused;
    /// The event number `from` counts from; null until a seed learns it.
    long? _base;
    readonly RemoteTranscriptCache? _cache;
    /// Every line loaded, in stream order, kept only for the cache.
    readonly List<ProjectedLine>? _loaded;
    /// The session refused this reader, so what it loaded is not kept.
    bool _lost;
    /// The cache's stamp when the loaded lines were read.
    (int Generation, int Refusals) _stamp;
    /// A snapshot taken from the cache, with the stamp it was handed out under, until a resume publishes or rejects
    /// it: a retry resumes it again, and a close before then puts it back.
    (RemoteTranscriptSnapshot Snapshot, (int Generation, int Refusals) Stamp)? _resume;
    /// Tail projections between reading the prompt state and committing their event: a snapshot taken
    /// then would pair prompts that already saw the event with a position that has not.
    int _projecting;
    /// How many pending lines the seed committed, and the position it ended at. A live event that
    /// lands before the pane drains the seed follows it in the next read: folding it into the Reset
    /// would place it inside the replayed history, where it can no longer acknowledge a send.
    int _resetCount;
    long? _seedBoundary;
    int _attempt;
    CancellationTokenSource? _tailCts;
    volatile bool _waitingToRetry;
    bool _disposed;

    internal event Action? Changed;
    internal bool HasPendingChanges { get { lock (_lock) return _pending.Count > 0 || _older.Count > 0 || _pendingStatus == FeedStatus.Reset || _verdict is not null && !_verdictRead; } }

    /// Whether events before the oldest loaded one remain on the server.
    internal bool HasOlder { get { lock (_lock) return _earliest > 0 && !_olderRefused; } }

    /// Whether the next older page is loaded without the reader asking for it.
    internal bool PrefetchesOlder { get { lock (_lock) return _earliest > 0 && !_olderRefused && _position - _oldest + 1 < _prefetch; } }

    internal Task? PendingRunForTesting { get; private set; }
    internal Task? PendingOlderForTesting { get; private set; }
    internal bool WaitingToRetryForTesting => _waitingToRetry;

    public RemoteTranscriptFeed(
            string sessionId, string vendor, IObservable<SessionAccessState> access, SessionEventsReader readEvents,
            IServerLane lane, TimeProvider time, Action<string> log, int? window = Window, RemoteTranscriptCache? cache = null,
            int prefetch = Prefetch) {
        _sessionId = sessionId;
        _window = window;
        _prefetch = prefetch;
        _cache = cache;
        _loaded = cache is null ? null : [];
        _rules = TranscriptChat.RulesFor(vendor);
        _readEvents = readEvents;
        _lane = lane;
        _time = time;
        _log = log;
        _access = access.Subscribe(state => {
            if (state == SessionAccessState.Established) Restart();
            else StopTail();
            if (state == SessionAccessState.Denied) Forget();
        });
    }

    public long? CurrentOffset {
        get { lock (_lock) return CurrentOffsetLocked(); }
    }

    public FeedRead ReadAppended() {
        lock (_lock) {
            if (_pendingStatus == FeedStatus.Reset) {
                var seed = _pending.GetRange(0, _resetCount);
                _pending.RemoveRange(0, _resetCount);
                var boundary = _seedBoundary;
                _delivered = Math.Max(_delivered, (boundary ?? 0) - 1);
                _resetCount = 0;
                _seedBoundary = null;
                _pendingStatus = FeedStatus.Ok;
                return new(FeedStatus.Reset, seed, boundary);
            }
            if (_pending.Count > 0 || _older.Count > 0) {
                var lines = _pending.ToArray();
                var older = _older.ToArray();
                if (lines.Length > 0) _delivered = Math.Max(_delivered, lines[^1].Offset);
                _pending.Clear();
                _older.Clear();
                return new(FeedStatus.Ok, lines) { Older = older };
            }
            if (_verdict is { } verdict) {
                _verdictRead = true;
                return new(verdict.Status, [], null, verdict.Failure);
            }
            return new(FeedStatus.Ok, []);
        }
    }

    long? CurrentOffsetLocked() => _position is { } p ? p + 1 : null;

    void Restart() {
        lock (_lock) {
            if (_disposed) return;
            _tailCts?.Cancel();
            _verdict = null;
            _olderRefused = false;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _tailCts = cts;
            var attempt = ++_attempt;
            var run = Task.Run(() => RunAsync(attempt, cts.Token));
            PendingRunForTesting = run.ContinueWith(_ => {
                lock (_lock) { if (ReferenceEquals(_tailCts, cts)) _tailCts = null; }
                cts.Dispose();
            }, TaskScheduler.Default);
        }
        Changed?.Invoke();
    }

    void StopTail() {
        lock (_lock) {
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
        }
    }

    bool IsCurrent(int attempt) { lock (_lock) return _attempt == attempt; }

    async Task RunAsync(int attempt, CancellationToken ct) {
        var failures = 0;
        while (!ct.IsCancellationRequested && IsCurrent(attempt)) {
            try {
                if (!await EnsureSeededAsync(attempt, ct).ConfigureAwait(false)) return;
                if (await TailAsync(attempt, ct).ConfigureAwait(false)) failures = 0;
            } catch (OperationCanceledException) {
                return;
            } catch (HubException ex) {
                // The hub refused the subscribe, and a refusal it repeats is not worth retrying:
                // report it and stop, rather than tailing a stream this caller cannot read.
                Enqueue(attempt, FeedStatus.Failed, ex.Message.Contains(WireTokens.StreamNotAuthorized, StringComparison.Ordinal)
                    ? "not authorized to read this session's stream"
                    : ex.Message);
                return;
            } catch (Exception ex) {
                LogOnce(attempt, $"remote transcript: {ex.Message}");
            }
            // The tail ended without a cancel: the connection dropped, or a newer subscribe on the
            // same stream replaced it. A superseded attempt stops; the current one waits and resumes.
            if (!IsCurrent(attempt)) return;
            var delay = Retry[Math.Min(failures++, Retry.Length - 1)];
            var wait = Task.Delay(delay, _time, ct);
            _waitingToRetry = true;
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            finally { _waitingToRetry = false; }
        }
    }

    /// True once a seed is in place. Both refusals stop the run and say so: a hidden session is
    /// Missing, an unauthorized read Failed — otherwise the pane shows an empty transcript with
    /// nothing to explain it. A windowed seed asks for the stream's tail; a server that does not know
    /// the parameter answers from the oldest event instead, and that page stands in for a probe of
    /// where the stream ends, since `from` counts from the oldest event the stream still holds.
    async Task<bool> EnsureSeededAsync(int attempt, CancellationToken ct) {
        lock (_lock) if (_position is not null) return true;
        var stamp = _cache?.Stamp(_sessionId) ?? default;
        (RemoteTranscriptSnapshot Snapshot, (int Generation, int Refusals) Stamp)? kept;
        lock (_lock) kept = _resume;
        if (kept is null && _cache?.Take(_sessionId) is { } taken) {
            // The cache is keyed by session alone: lines a windowed feed kept would show part of the session as all of it.
            if (_window is null && taken.Earliest > 0) _cache.Put(_sessionId, taken, stamp);
            else {
                kept = (taken, stamp);
                bool closed;
                lock (_lock) {
                    closed = _disposed;
                    if (!closed) _resume = kept;
                }
                // Outside the lock: the cache publishes under its own lock and then takes this one.
                if (closed) {
                    _cache.Put(_sessionId, taken, stamp);
                    return false;
                }
            }
        }
        if (kept is { } resume) {
            var resumed = await ResumeAsync(attempt, resume.Stamp, resume.Snapshot, ct).ConfigureAwait(false);
            if (resumed is { } done) return done;
            // An earlier attempt's answer leaves the snapshot to the current one.
            lock (_lock) if (_attempt == attempt) _resume = null;
            stamp = _cache!.Stamp(_sessionId);
        }
        var lines = new List<ProjectedLine>();
        var scheduled = NewScheduled();
        long from = 0;
        long? oldest;
        StreamEventsFetch fetch;
        if (_window is { } window) {
            fetch = await _readEvents(_sessionId, 0, null, window, ct).ConfigureAwait(false);
            if (!Admit(attempt, fetch, out var probe)) return false;
            if (probe.FirstFrom is { } firstFrom) {
                from = firstFrom;
                oldest = ProjectInto(probe, lines, scheduled);
                // A page that starts past the oldest event starts exactly window events before the end, which
                // places the oldest event even when the page's own first event is withheld from this reader.
                long? origin = firstFrom > 0 ? probe.LastEventNumber - window + 1 - firstFrom : probe.Events is [var head, ..] ? head.EventNumber : null;
                if (origin is not null) lock (_lock) _base = origin;
            } else {
                if (probe.Events is [var first, ..]) {
                    lock (_lock) _base = first.EventNumber;
                    if (probe.NextFrom is not null) from = Math.Max(0, probe.LastEventNumber - first.EventNumber + 1 - window);
                }
                if (from == 0 && probe.NextFrom is null) oldest = ProjectInto(probe, lines, scheduled);
                else (fetch, oldest) = await ReadForwardAsync(from, lines, scheduled, ct).ConfigureAwait(false);
            }
        } else {
            (fetch, oldest) = await ReadForwardAsync(0, lines, scheduled, ct).ConfigureAwait(false);
        }
        if (!Admit(attempt, fetch, out var page)) return false;
        lock (_lock) {
            if (_attempt != attempt) return false;
            _verdict = null;
            _stamp = stamp;
            _scheduled = scheduled;
            _position = page.LastEventNumber;
            _earliest = from;
            _oldest = oldest;
            // A read from 0 starts at the oldest event held. Left null on an empty stream, the first live event sets both.
            if (_window is null) _base = oldest;
            _older.Clear();
            _loaded?.Clear();
            _loaded?.AddRange(lines);
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
            _resetCount = lines.Count;
            _seedBoundary = CurrentOffsetLocked();
        }
        Changed?.Invoke();
        return true;
    }

    /// The kept lines and the events after them go out together as the seed, once the server has answered
    /// for this reader: a refusal shows none of them, and a send made now cannot be acknowledged by an event
    /// recorded while the transcript was closed. The read starts at the last kept event, which has to come
    /// back first; anything else means the stream's oldest events aged out, and a fresh seed is read.
    async Task<bool?> ResumeAsync(int attempt, (int Generation, int Refusals) stamp, RemoteTranscriptSnapshot snapshot, CancellationToken ct) {
        var fetch = await _readEvents(_sessionId, snapshot.Position - snapshot.Base, MaxPage, null, ct).ConfigureAwait(false);
        if (!Admit(attempt, fetch, out var page)) return false;
        if (page.Events is not [var first, ..] || first.EventNumber != snapshot.Position) return null;
        var lines = new List<ProjectedLine>(snapshot.Lines);
        // A copy, so an attempt that fails part way leaves the snapshot as it was for the retry.
        var scheduled = snapshot.Scheduled.Copy();
        ProjectInto(page, lines, scheduled, above: snapshot.Position);
        if (page is { NextFrom: { } next, Events: [.., var tail] }) {
            (fetch, _) = await ReadForwardAsync(next - 1, lines, scheduled, ct, after: tail.EventNumber).ConfigureAwait(false);
            if (!Admit(attempt, fetch, out page)) return false;
        }
        // The account can change while the read is out; what it returned then belongs to the earlier one.
        var current = false;
        if (!_cache!.WhileCurrent(_sessionId, stamp, () => current = PublishResumed(attempt, stamp, snapshot, page, lines, scheduled))) return null;
        if (!current) return false;
        Changed?.Invoke();
        return true;
    }

    bool PublishResumed(int attempt, (int Generation, int Refusals) stamp, RemoteTranscriptSnapshot snapshot, StreamEventsPage page,
            List<ProjectedLine> lines, ScheduledPrompts scheduled) {
        lock (_lock) {
            if (_attempt != attempt) return false;
            _resume = null;
            _verdict = null;
            _stamp = stamp;
            _scheduled = scheduled;
            _position = Math.Max(snapshot.Position, page.LastEventNumber);
            _earliest = snapshot.Earliest;
            _oldest = snapshot.Oldest;
            _base = snapshot.Base;
            _older.Clear();
            _loaded!.Clear();
            _loaded.AddRange(lines);
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
            _resetCount = lines.Count;
            _seedBoundary = CurrentOffsetLocked();
        }
        return true;
    }

    bool Admit(int attempt, StreamEventsFetch fetch, out StreamEventsPage page) {
        page = fetch.Page!;
        if (!IsCurrent(attempt)) return false;
        if (fetch.NotFound) { Enqueue(attempt, FeedStatus.Missing); return false; }
        if (fetch.Unauthorized) { Enqueue(attempt, FeedStatus.Failed, "not signed in"); return false; }
        if (fetch.Page is null) throw new InvalidOperationException(fetch.Failure ?? "session events unavailable");
        return true;
    }

    /// Pages forward from a count to the stream's end, returning the last fetch, or the first that failed,
    /// and the number of the first event read.
    /// Each later page starts one event early: that event coming back first shows the count still lines up.
    /// When it does not, events aged out between pages, and the page is asked for again that much earlier.
    /// <paramref name="after"/> is the last event a caller already read, which the first page then overlaps.
    async Task<(StreamEventsFetch Fetch, long? First)> ReadForwardAsync(long from, List<ProjectedLine> lines, ScheduledPrompts scheduled,
            CancellationToken ct, long? after = null) {
        long? first = null;
        long? last = after;
        var realigned = 0;
        while (true) {
            var fetch = await _readEvents(_sessionId, from, MaxPage, null, ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) return (fetch, first);
            // Events can age out again while a corrected page is asked for, so the check runs on every retry.
            if (last is { } seen && realigned < MaxRealignments && from > 0) {
                if (page.Events is [var lead, ..] && lead.EventNumber > seen) {
                    from = Math.Max(0, from - (lead.EventNumber - seen));
                    realigned++;
                    continue;
                }
                // So many events aged out that the count ran past the end: only the oldest event held can place it.
                if (page.Events is null or [] && page.LastEventNumber > seen) {
                    var probe = await _readEvents(_sessionId, 0, 1, null, ct).ConfigureAwait(false);
                    if (probe.Page is not { Events: [var held, ..] }) return (probe, first);
                    from = Math.Max(0, seen - held.EventNumber);
                    realigned++;
                    continue;
                }
            }
            realigned = 0;
            var number = ProjectInto(page, lines, scheduled, above: last ?? long.MinValue);
            first ??= number;
            if (page.Events is [.., var tail]) last = Math.Max(last ?? tail.EventNumber, tail.EventNumber);
            if (page.NextFrom is not { } next || next <= from) return (fetch, first);
            from = next - 1;
        }
    }

    /// The number of the page's first event, if it has one.
    long? ProjectInto(StreamEventsPage page, List<ProjectedLine> lines, ScheduledPrompts scheduled, long below = long.MaxValue, long above = long.MinValue) {
        long? first = null;
        foreach (var evt in page.Events ?? []) {
            if (evt.EventNumber >= below || evt.EventNumber <= above) continue;
            first ??= evt.EventNumber;
            if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.Vendors, null, evt.EventNumber, evt.Timestamp, scheduled) is { } line)
                lines.Add(line);
        }
        return first;
    }

    static ScheduledPrompts NewScheduled() => new() { Declined = new HashSet<string>(StringComparer.Ordinal) };

    /// Starts reading the page before the oldest loaded event, unless one is already on its way or none
    /// remains. Its lines arrive as the next read's Older.
    internal bool RequestOlder() {
        lock (_lock) {
            if (_disposed || _olderRun is not null || _olderRefused || _earliest is not (> 0 and var earliest)) return false;
            var token = _lifetime.Token;
            var oldest = _oldest;
            var attempt = _attempt;
            var run = Task.Run(() => LoadOlderAsync(attempt, earliest, oldest, token));
            _olderRun = run;
            PendingOlderForTesting = run.ContinueWith(_ => { lock (_lock) if (ReferenceEquals(_olderRun, run)) _olderRun = null; }, TaskScheduler.Default);
        }
        return true;
    }

    /// Events past the oldest loaded one in the reply mean the stream's oldest events aged out since it
    /// was counted: the count is worked out again from the reply and the page read once more.
    /// A failed read waits on the tail's ladder and tries again while the feed lives; a refusal stops it.
    async Task LoadOlderAsync(int attempt, long earliest, long? oldest, CancellationToken ct) {
        var failures = 0;
        while (!ct.IsCancellationRequested) {
            try {
                // A page that projects nothing leaves the pane nothing to scroll for, so the next one follows.
                while (await LoadOlderPageAsync(attempt, earliest, oldest, ct).ConfigureAwait(false) is { } next) (earliest, oldest) = next;
                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                if (failures == 0) _log($"remote transcript: older events: {ex.Message}");
            }
            lock (_lock) if (_disposed || _earliest != earliest) return;
            try { await Task.Delay(Retry[Math.Min(failures++, Retry.Length - 1)], _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// The count and oldest event to continue from when the page committed no lines and older events remain.
    async Task<(long Earliest, long? Oldest)?> LoadOlderPageAsync(int attempt, long earliest, long? oldest, CancellationToken ct) {
        var count = earliest;
        var window = _window ?? MaxPage;
        for (var tries = 0; ; tries++) {
            var from = Math.Max(0, count - window);
            var fetch = await _readEvents(_sessionId, from, (int)(count - from), null, ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) return Refused(attempt, fetch);
            if (tries == 0 && oldest is { } known && page.Events is [var first, ..] && page.Events.Any(e => e.EventNumber >= known)) {
                count = Math.Max(0, known - (first.EventNumber - from));
                if (count > 0) continue;
                Commit(earliest, 0, oldest, []);
                return null;
            }
            var scheduled = NewScheduled();
            var lines = new List<ProjectedLine>();
            var firstNumber = ProjectInto(page, lines, scheduled, below: oldest ?? long.MaxValue);
            ScheduledPrompts current;
            lock (_lock) current = _scheduled;
            // One lock for the check and the merge: a live line asked about between them would miss both.
            bool reclassifies;
            lock (current) {
                reclassifies = scheduled.AwaitsCompactEcho || scheduled.Prompts.Any(current.Declined!.Contains);
                if (!reclassifies) {
                    current.Include(scheduled);
                    current.Declined!.UnionWith(scheduled.Declined!);
                }
            }
            if (reclassifies) {
                await RereadAsync(attempt, earliest, from, ct).ConfigureAwait(false);
                return null;
            }
            if (!Commit(earliest, from, firstNumber ?? oldest, lines) || lines.Count > 0 || from == 0) return null;
            return (from, firstNumber ?? oldest);
        }
    }

    /// A commit that ends the run frees the next request before the pane hears of it, so a request made
    /// while handling the change is not turned away.
    bool Commit(long earliest, long from, long? oldest, List<ProjectedLine> lines) {
        var ends = lines.Count > 0 || from == 0;
        lock (_lock) {
            if (_disposed || _earliest != earliest) return false;
            _earliest = from;
            _oldest = oldest;
            _older.InsertRange(0, lines);
            if (ends) _olderRun = null;
            _loaded?.InsertRange(0, lines);
        }
        if (ends) Changed?.Invoke();
        return true;
    }

    /// Null for a refusal, which stops paging and refuses the session; any other failure throws, for the retry.
    /// A refusal answering a request made under an earlier grant of access says nothing about the current one.
    (long Earliest, long? Oldest)? Refused(int attempt, StreamEventsFetch fetch) {
        if (fetch is { NotFound: false, Unauthorized: false }) throw new InvalidOperationException(fetch.Failure ?? "could not read older events");
        lock (_lock) {
            if (_attempt != attempt) return null;
            _olderRefused = true;
        }
        Forget();
        _log($"remote transcript: older events: {(fetch.NotFound ? "not found" : "not signed in")}");
        return null;
    }

    /// Everything loaded, read again from the new oldest event under the prompts it schedules, and
    /// delivered as a seed. Lines the pane has not drained yet stay out of that history and follow it,
    /// so a send their echo acknowledges is still acknowledged.
    async Task RereadAsync(int attempt, long earliest, long from, CancellationToken ct) {
        var scheduled = NewScheduled();
        var lines = new List<ProjectedLine>();
        lock (_lock) _liveDuringReread = [];
        StreamEventsFetch fetch;
        long? oldest;
        try {
            (fetch, oldest) = await ReadForwardAsync(from, lines, scheduled, ct).ConfigureAwait(false);
        } catch {
            lock (_lock) _liveDuringReread = null;
            throw;
        }
        if (fetch.Page is not { } page) {
            lock (_lock) _liveDuringReread = null;
            Refused(attempt, fetch);
            return;
        }
        lock (_lock) {
            var live = _liveDuringReread ?? [];
            _liveDuringReread = null;
            if (_disposed || _earliest != earliest || _position is not { } position) return;
            var reached = Math.Max(page.LastEventNumber, lines.Count > 0 ? lines[^1].Offset : -1);
            lines.AddRange(live.Where(line => line.Offset > reached));
            var undrained = _pendingStatus == FeedStatus.Reset ? _pending.Skip(_resetCount) : _pending;
            reached = Math.Max(reached, lines.Count > 0 ? lines[^1].Offset : -1);
            var later = undrained.Where(line => line.Offset > reached).ToList();
            var boundary = _delivered;
            var history = lines.Where(line => line.Offset <= boundary).ToList();
            // Live events past the reply were read under the state being replaced; what they scheduled stays.
            lock (_scheduled) {
                scheduled.Include(_scheduled);
                scheduled.Declined!.UnionWith(_scheduled.Declined!);
            }
            _scheduled = scheduled;
            _olderRun = null;
            _earliest = from;
            _oldest = oldest;
            _position = Math.Max(position, reached);
            _older.Clear();
            _pending.Clear();
            _pending.AddRange(history);
            _resetCount = history.Count;
            _seedBoundary = boundary + 1;
            _pending.AddRange(lines.Where(line => line.Offset > boundary));
            _pending.AddRange(later);
            if (_loaded is not null) {
                var kept = _loaded.Where(line => line.Offset > reached).ToList();
                _loaded.Clear();
                _loaded.AddRange(lines);
                _loaded.AddRange(kept);
            }
            _pendingStatus = FeedStatus.Reset;
        }
        Changed?.Invoke();
    }

    /// True when the tail delivered at least one envelope, which is what resets the retry ladder;
    /// a tail that ends without ever yielding keeps the caller's failure count climbing.
    async Task<bool> TailAsync(int attempt, CancellationToken ct) {
        ulong? from;
        lock (_lock) from = _position is >= 0 ? (ulong)_position : null;
        var received = false;
        await foreach (var envelope in _lane.TailStreamAsync(StreamNames.AgentSession(_sessionId), from, ct).ConfigureAwait(false)) {
            var offset = (long)envelope.StreamPosition;
            // A re-read can take the event, or replace the prompts it is read under, while it projects.
            while (true) {
                ScheduledPrompts scheduled;
                // A re-tail after a reconnect can overlap a subscription the server still holds.
                lock (_lock) {
                    if (_position is { } seen && offset <= seen) break;
                    scheduled = _scheduled;
                    _projecting++;
                }
                ProjectedLine? line;
                try {
                    line = Project(envelope.EventType, envelope.JsonPayload, null, envelope.JsonMetadata, offset, envelope.Timestamp, scheduled);
                } catch {
                    lock (_lock) _projecting--;
                    throw;
                }
                lock (_lock) {
                    _projecting--;
                    if (_attempt != attempt) return received;
                    if (_position is { } taken && offset <= taken) break;
                    if (!ReferenceEquals(scheduled, _scheduled)) continue;
                    // Once per outage: the stream delivering again ends it, however this tail later ends.
                    if (!received) _logged.Clear();
                    received = true;
                    if (_base is null && _oldest is null) _base = _oldest = offset;
                    _position = offset;
                    if (line is { } l) {
                        _pending.Add(l);
                        _liveDuringReread?.Add(l);
                        _loaded?.Add(l);
                    }
                }
                Changed?.Invoke();
                break;
            }
        }
        return received;
    }

    ProjectedLine? Project(
            string eventType, string json, JsonElement? vendors, string? metadata, long offset, DateTimeOffset? timestamp, ScheduledPrompts scheduled) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        _rules?.RestoreServerFacts(payload, vendors, metadata);
        var at = CanonicalEventTime.Of(payload) ?? timestamp ?? _time.GetUtcNow();
        ChatProjectionResult projected;
        lock (scheduled) projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, at), _rules, scheduled);
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 && projected.LiteralInputs.Count == 0 && projected.Runs.Count == 0 ? null : new(projected, offset);
    }

    /// A verdict belongs to the attempt that reached it: one landing after its run was stopped
    /// must not become the current run's.
    void Enqueue(int attempt, FeedStatus status, string? failure = null) {
        lock (_lock) {
            if (_attempt != attempt) return;
            _verdict = (status, failure);
            _verdictRead = false;
        }
        if (status is FeedStatus.Missing or FeedStatus.Failed) Forget();
        Changed?.Invoke();
    }

    void Forget() {
        lock (_lock) {
            _lost = true;
            _resume = null;
        }
        _cache?.Drop(_sessionId);
    }

    /// The newest lines up to the cache's limit; the stream events before the oldest kept one count as unloaded.
    /// A transcript whose turned-down prompt checks outgrow that limit is not kept: they hold message text.
    /// Nor is a whole-session feed that outgrows it, which would reopen showing part of the session as all of it.
    RemoteTranscriptSnapshot? SnapshotLocked() {
        if (_lost || _projecting > 0 || _loaded is null || _position is not { } position || _base is not { } origin || _earliest is not { } earliest) return null;
        if (_window is null && _loaded.Count > RemoteTranscriptCache.MaxLines) return null;
        // A copy: an older page still projecting merges into the live state after the feed has closed.
        ScheduledPrompts scheduled;
        lock (_scheduled) {
            if (_scheduled.Declined!.Count > RemoteTranscriptCache.MaxLines) return null;
            scheduled = _scheduled.Copy();
        }
        var skip = Math.Max(0, _loaded.Count - RemoteTranscriptCache.MaxLines);
        var lines = _loaded.GetRange(skip, _loaded.Count - skip);
        var oldest = _oldest;
        if (skip > 0) {
            earliest = Math.Max(earliest, lines[0].Offset - origin);
            oldest = lines[0].Offset;
        }
        return new(lines, position, earliest, oldest, origin, scheduled);
    }

    /// A superseded attempt's failure would mark a reason the current attempt has not hit.
    void LogOnce(int attempt, string reason) {
        bool first;
        lock (_lock) first = _attempt == attempt && _logged.Add(reason);
        if (first) _log(reason);
    }

    public void Dispose() {
        RemoteTranscriptSnapshot? snapshot;
        (RemoteTranscriptSnapshot Snapshot, (int Generation, int Refusals) Stamp)? unused;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
            snapshot = SnapshotLocked();
            unused = snapshot is null && !_lost ? _resume : null;
        }
        if (snapshot is not null) _cache!.Put(_sessionId, snapshot, _stamp);
        else if (unused is { } kept) _cache!.Put(_sessionId, kept.Snapshot, kept.Stamp);
        _access.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
