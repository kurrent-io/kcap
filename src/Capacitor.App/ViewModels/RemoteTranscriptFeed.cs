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
    internal bool HasOlder { get { lock (_lock) return _earliest > 0; } }

    /// Whether the next older page is loaded without the reader asking for it.
    internal bool PrefetchesOlder { get { lock (_lock) return _earliest > 0 && _position - _oldest + 1 < _prefetch; } }

    internal Task? PendingRunForTesting { get; private set; }
    internal Task? PendingOlderForTesting { get; private set; }
    internal bool WaitingToRetryForTesting => _waitingToRetry;

    public RemoteTranscriptFeed(
            string sessionId, string vendor, IObservable<SessionAccessState> access, SessionEventsReader readEvents,
            IServerLane lane, TimeProvider time, Action<string> log, int? window = Window, int prefetch = Prefetch) {
        _sessionId = sessionId;
        _window = window;
        _prefetch = prefetch;
        _rules = TranscriptChat.RulesFor(vendor);
        _readEvents = readEvents;
        _lane = lane;
        _time = time;
        _log = log;
        _access = access.Subscribe(state => {
            if (state == SessionAccessState.Established) Restart();
            else StopTail();
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
    /// nothing to explain it. A one-event probe finds where the stream ends: `from` counts from the
    /// oldest event the stream still holds, and no other route reports where it ends.
    async Task<bool> EnsureSeededAsync(int attempt, CancellationToken ct) {
        lock (_lock) if (_position is not null) return true;
        var lines = new List<ProjectedLine>();
        var scheduled = NewScheduled();
        long from = 0;
        long? oldest;
        StreamEventsFetch fetch;
        if (_window is { } window) {
            fetch = await _readEvents(_sessionId, 0, 1, ct).ConfigureAwait(false);
            if (!Admit(attempt, fetch, out var probe)) return false;
            if (probe.NextFrom is not null && probe.Events is [var first, ..])
                from = Math.Max(0, probe.LastEventNumber - first.EventNumber + 1 - window);
            if (from == 0 && probe.NextFrom is null) oldest = ProjectInto(probe, lines, scheduled);
            else (fetch, oldest) = await ReadForwardAsync(from, lines, scheduled, ct).ConfigureAwait(false);
        } else {
            (fetch, oldest) = await ReadForwardAsync(0, lines, scheduled, ct).ConfigureAwait(false);
        }
        if (!Admit(attempt, fetch, out var page)) return false;
        lock (_lock) {
            if (_attempt != attempt) return false;
            _verdict = null;
            _scheduled = scheduled;
            _position = page.LastEventNumber;
            _earliest = from;
            _oldest = oldest;
            _older.Clear();
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
            _resetCount = lines.Count;
            _seedBoundary = CurrentOffsetLocked();
        }
        Changed?.Invoke();
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
    async Task<(StreamEventsFetch Fetch, long? First)> ReadForwardAsync(long from, List<ProjectedLine> lines, ScheduledPrompts scheduled, CancellationToken ct) {
        long? first = null;
        long? last = null;
        var realigned = false;
        while (true) {
            var fetch = await _readEvents(_sessionId, from, MaxPage, ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) return (fetch, first);
            if (last is { } seen && !realigned && from > 0 && page.Events is [var lead, ..] && lead.EventNumber > seen) {
                from = Math.Max(0, from - (lead.EventNumber - seen));
                realigned = true;
                continue;
            }
            realigned = false;
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
            if (_disposed || _olderRun is not null || _earliest is not (> 0 and var earliest)) return false;
            var token = _lifetime.Token;
            var oldest = _oldest;
            var run = Task.Run(() => LoadOlderAsync(earliest, oldest, token));
            _olderRun = run;
            PendingOlderForTesting = run.ContinueWith(_ => { lock (_lock) if (ReferenceEquals(_olderRun, run)) _olderRun = null; }, TaskScheduler.Default);
        }
        return true;
    }

    /// Events past the oldest loaded one in the reply mean the stream's oldest events aged out since it
    /// was counted: the count is worked out again from the reply and the page read once more.
    /// A failed read waits on the tail's ladder and tries again while the feed lives; a refusal stops it.
    async Task LoadOlderAsync(long earliest, long? oldest, CancellationToken ct) {
        var failures = 0;
        while (!ct.IsCancellationRequested) {
            try {
                // A page that projects nothing leaves the pane nothing to scroll for, so the next one follows.
                while (await LoadOlderPageAsync(earliest, oldest, ct).ConfigureAwait(false) is { } next) (earliest, oldest) = next;
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
    async Task<(long Earliest, long? Oldest)?> LoadOlderPageAsync(long earliest, long? oldest, CancellationToken ct) {
        var count = earliest;
        var window = _window ?? MaxPage;
        for (var tries = 0; ; tries++) {
            var from = Math.Max(0, count - window);
            var fetch = await _readEvents(_sessionId, from, (int)(count - from), ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) return Refused(fetch);
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
                await RereadAsync(earliest, from, ct).ConfigureAwait(false);
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
        }
        if (ends) Changed?.Invoke();
        return true;
    }

    /// Null for a refusal, which stops paging; any other failure throws, for the retry.
    (long Earliest, long? Oldest)? Refused(StreamEventsFetch fetch) {
        if (fetch is { NotFound: false, Unauthorized: false }) throw new InvalidOperationException(fetch.Failure ?? "could not read older events");
        _log($"remote transcript: older events: {(fetch.NotFound ? "not found" : "not signed in")}");
        return null;
    }

    /// Everything loaded, read again from the new oldest event under the prompts it schedules, and
    /// delivered as a seed. Lines the pane has not drained yet stay out of that history and follow it,
    /// so a send their echo acknowledges is still acknowledged.
    async Task RereadAsync(long earliest, long from, CancellationToken ct) {
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
            Refused(fetch);
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
                }
                var line = Project(envelope.EventType, envelope.JsonPayload, null, envelope.JsonMetadata, offset, envelope.Timestamp, scheduled);
                lock (_lock) {
                    if (_attempt != attempt) return received;
                    if (_position is { } taken && offset <= taken) break;
                    if (!ReferenceEquals(scheduled, _scheduled)) continue;
                    // Once per outage: the stream delivering again ends it, however this tail later ends.
                    if (!received) _logged.Clear();
                    received = true;
                    _position = offset;
                    if (line is { } l) {
                        _pending.Add(l);
                        _liveDuringReread?.Add(l);
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
        Changed?.Invoke();
    }

    /// A superseded attempt's failure would mark a reason the current attempt has not hit.
    void LogOnce(int attempt, string reason) {
        bool first;
        lock (_lock) first = _attempt == attempt && _logged.Add(reason);
        if (first) _log(reason);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
        }
        _access.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
