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

    readonly string _sessionId;
    /// Null seeds the whole session.
    readonly int? _window;
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

    internal Task? PendingRunForTesting { get; private set; }
    internal Task? PendingOlderForTesting { get; private set; }
    internal bool WaitingToRetryForTesting => _waitingToRetry;

    public RemoteTranscriptFeed(
            string sessionId, string vendor, IObservable<SessionAccessState> access, SessionEventsReader readEvents,
            IServerLane lane, TimeProvider time, Action<string> log, int? window = Window) {
        _sessionId = sessionId;
        _window = window;
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
                _resetCount = 0;
                _seedBoundary = null;
                _pendingStatus = FeedStatus.Ok;
                return new(FeedStatus.Reset, seed, boundary);
            }
            if (_pending.Count > 0 || _older.Count > 0) {
                var lines = _pending.ToArray();
                var older = _older.ToArray();
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
    /// nothing to explain it. A one-event probe finds where the stream ends, because `from` counts
    /// from the oldest event the stream still holds rather than from event number zero.
    async Task<bool> EnsureSeededAsync(int attempt, CancellationToken ct) {
        lock (_lock) if (_position is not null) return true;
        var lines = new List<ProjectedLine>();
        var scheduled = NewScheduled();
        long from = 0;
        StreamEventsFetch fetch;
        if (_window is { } window) {
            fetch = await _readEvents(_sessionId, 0, 1, ct).ConfigureAwait(false);
            if (!Admit(attempt, fetch, out var probe)) return false;
            if (probe.NextFrom is not null && probe.Events is [var oldest, ..])
                from = Math.Max(0, probe.LastEventNumber - oldest.EventNumber + 1 - window);
            if (from == 0 && probe.NextFrom is null) ProjectInto(probe, lines, scheduled);
            else fetch = await ReadForwardAsync(from, lines, scheduled, ct).ConfigureAwait(false);
        } else {
            fetch = await ReadForwardAsync(0, lines, scheduled, ct).ConfigureAwait(false);
        }
        if (!Admit(attempt, fetch, out var page)) return false;
        lock (_lock) {
            if (_attempt != attempt) return false;
            _verdict = null;
            _scheduled = scheduled;
            _position = page.LastEventNumber;
            _earliest = from;
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

    /// Pages forward from a count to the stream's end, returning the last fetch, or the first that failed.
    async Task<StreamEventsFetch> ReadForwardAsync(long from, List<ProjectedLine> lines, ScheduledPrompts scheduled, CancellationToken ct) {
        while (true) {
            var fetch = await _readEvents(_sessionId, from, MaxPage, ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) return fetch;
            ProjectInto(page, lines, scheduled);
            if (page.NextFrom is not { } next || next <= from) return fetch;
            from = next;
        }
    }

    void ProjectInto(StreamEventsPage page, List<ProjectedLine> lines, ScheduledPrompts scheduled) {
        foreach (var evt in page.Events ?? [])
            if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.Vendors, null, evt.EventNumber, evt.Timestamp, scheduled) is { } line)
                lines.Add(line);
    }

    static ScheduledPrompts NewScheduled() => new() { Declined = new HashSet<string>(StringComparer.Ordinal) };

    /// Starts reading the page before the oldest loaded event, unless one is already on its way or none
    /// remains. Its lines arrive as the next read's Older.
    internal bool RequestOlder() {
        lock (_lock) {
            if (_disposed || _olderRun is not null || _earliest is not (> 0 and var earliest)) return false;
            var token = _lifetime.Token;
            var run = Task.Run(() => LoadOlderAsync(earliest, token));
            _olderRun = run;
            PendingOlderForTesting = run.ContinueWith(_ => { lock (_lock) _olderRun = null; }, TaskScheduler.Default);
        }
        return true;
    }

    async Task LoadOlderAsync(long earliest, CancellationToken ct) {
        try {
            var from = Math.Max(0, earliest - (_window ?? MaxPage));
            var fetch = await _readEvents(_sessionId, from, (int)(earliest - from), ct).ConfigureAwait(false);
            if (fetch.Page is not { } page) {
                LogOlder(fetch);
                return;
            }
            var scheduled = NewScheduled();
            var lines = new List<ProjectedLine>();
            ProjectInto(page, lines, scheduled);
            ScheduledPrompts current;
            lock (_lock) current = _scheduled;
            bool reclassifies;
            lock (current) reclassifies = scheduled.AwaitsCompactEcho || scheduled.Prompts.Any(current.Declined!.Contains);
            if (reclassifies) {
                await RereadAsync(earliest, from, ct).ConfigureAwait(false);
                return;
            }
            lock (current) {
                current.Include(scheduled);
                current.Declined!.UnionWith(scheduled.Declined!);
            }
            lock (_lock) {
                if (_disposed || _earliest != earliest) return;
                _earliest = from;
                _older.InsertRange(0, lines);
            }
            Changed?.Invoke();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            _log($"remote transcript: older events: {ex.Message}");
        }
    }

    void LogOlder(StreamEventsFetch fetch) =>
        _log($"remote transcript: older events: {fetch.Failure ?? (fetch.NotFound ? "not found" : "not signed in")}");

    /// Everything loaded, read again from the new oldest event under the prompts it schedules, and
    /// delivered as a seed. Live lines past what the read reached still follow it.
    async Task RereadAsync(long earliest, long from, CancellationToken ct) {
        var scheduled = NewScheduled();
        var lines = new List<ProjectedLine>();
        var fetch = await ReadForwardAsync(from, lines, scheduled, ct).ConfigureAwait(false);
        if (fetch.Page is not { } page) {
            LogOlder(fetch);
            return;
        }
        lock (_lock) {
            if (_disposed || _earliest != earliest || _position is not { } position) return;
            var reached = Math.Max(page.LastEventNumber, lines.Count > 0 ? lines[^1].Offset : -1);
            var live = _pendingStatus == FeedStatus.Reset ? _pending.Skip(_resetCount) : _pending;
            var after = live.Where(line => line.Offset > reached).ToList();
            _scheduled = scheduled;
            _earliest = from;
            _position = Math.Max(position, reached);
            _older.Clear();
            _pending.Clear();
            _pending.AddRange(lines);
            _resetCount = lines.Count;
            _seedBoundary = reached + 1;
            _pending.AddRange(after);
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
            // A re-tail after a reconnect can overlap a subscription the server still holds.
            ScheduledPrompts scheduled;
            lock (_lock) {
                if (_position is { } seen && (long)envelope.StreamPosition <= seen) continue;
                scheduled = _scheduled;
            }
            var line = Project(envelope.EventType, envelope.JsonPayload, null, envelope.JsonMetadata, (long)envelope.StreamPosition, envelope.Timestamp, scheduled);
            lock (_lock) {
                if (_attempt != attempt) return received;
                // Once per outage: the stream delivering again ends it, however this tail later ends.
                if (!received) _logged.Clear();
                received = true;
                _position = (long)envelope.StreamPosition;
                if (line is { } l) _pending.Add(l);
            }
            Changed?.Invoke();
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
