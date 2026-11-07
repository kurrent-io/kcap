using System.Collections.Concurrent;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Records many sources in one process over one transport, one loop per source. Nothing outside the daemon
/// respawns a source that fails here, so the host retries it: the key stays in <see cref="ActiveKeys"/> and
/// <see cref="TryAdd"/> stays refused, and each attempt is a fresh recorder, context and stamper that resume
/// from the server's cursor. Retries wait 1s doubling to 30s and have no ceiling, because a source ends when its
/// owner calls <see cref="FinishAsync"/>. A recorder that returns cleanly ends its source and is never retried.
/// Given claims, the host beats a source's claim whenever it beats the source, and a source that lost its claim ends
/// where it stands, undrained and unretried, because it is no longer this host's. Taking and releasing a claim stay
/// with the host's owner: only it can tell a session that ended from a restart that must keep the source owned.
/// A source that ends on any path detaches from the transport, best-effort, because its connection may outlive it.
/// The owner hears how each attempt ended on the source's own loop, so it must not wait there for that source.
/// A server stop ends a backoff as a finish does, and a final drain after one is in a hurry. An abandoned source ends as
/// one that lost its claim does, whatever it was doing, and leaves nothing behind to send it later.
/// A source the owner says doesn't keep its claim offline is beaten only while the transport is up, so a backoff, an
/// outage or a connect the server never answers lets that claim go stale, and a hook can take the source.
/// A source that goes <see cref="WedgedAfter"/> without a beat while the transport is up is stuck where no token reaches.
/// The host gives that attempt up, refusing it everything it might do if it wakes, and goes on as after a failure; one
/// stuck in its final drain is marked for import and ends, because a second drain could race it.
/// </summary>
public sealed class RecorderHost {
    static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxRetryDelay   = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DetachBudget    = TimeSpan.FromSeconds(5);

    // Kept back from a shutdown's sends, at most half its bound: the tail's read, and redaction, whose floor is a second.
    static readonly TimeSpan SpoolReserve = TimeSpan.FromSeconds(2);

    /// <summary>How long a source can go without a beat before the host takes it for stuck. A healthy source goes longest
    /// between beats in one send, which the daemon's transport gives up on at 45 seconds; its other waits are shorter or
    /// beat as they go. Short of a claim's grace, so the next attempt beats the claim before a hook can take it.</summary>
    public static readonly TimeSpan WedgedAfter = TimeSpan.FromSeconds(50);

    readonly IRecorderTransport _transport;
    readonly Func<RecordingSource, IRowStamper> _stampers;
    readonly Func<RecordingSource, Action, RecorderContext> _contexts;
    readonly TimeProvider _time;
    readonly Func<string, FileStream>? _openTranscript;
    readonly RecorderClaims? _claims;
    readonly Action<RecorderAttemptEnded>? _attemptEnded;
    readonly Func<RecordingSource, bool>? _keepsClaimOffline;
    readonly ConcurrentDictionary<string, Entry> _entries = new();
    readonly Lock _admission = new();
    bool _stopping;

    /// <param name="contexts">Builds each attempt's context, given that attempt's beat, for what runs long outside the
    /// recorder, such as its housekeeping: those beats are the attempt's too.</param>
    /// <param name="keepsClaimOffline">Whether a source's claim is beaten while the transport is down; every source's is
    /// when this is null.</param>
    public RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, Action, RecorderContext> contexts, TimeProvider time,
        RecorderClaims? claims = null, Action<RecorderAttemptEnded>? attemptEnded = null, Func<RecordingSource, bool>? keepsClaimOffline = null)
        : this(transport, stampers, contexts, time, null, claims, attemptEnded, keepsClaimOffline) { }

    internal RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, Action, RecorderContext> contexts, TimeProvider time,
        Func<string, FileStream>? openTranscript, RecorderClaims? claims = null, Action<RecorderAttemptEnded>? attemptEnded = null,
        Func<RecordingSource, bool>? keepsClaimOffline = null) {
        _transport         = transport;
        _stampers          = stampers;
        _contexts          = contexts;
        _time              = time;
        _openTranscript    = openTranscript;
        _claims            = claims;
        _attemptEnded      = attemptEnded;
        _keepsClaimOffline = keepsClaimOffline;

        _transport.StopRequested += OnStopRequestedAsync;
    }

    /// <summary>Replaces <see cref="WedgedAfter"/>, and <see cref="Timeout.InfiniteTimeSpan"/> watches for nothing: a test
    /// clock that jumps minutes at a step while a drain runs on real time would read it as stuck.</summary>
    internal TimeSpan Watchdog { get; init; } = WedgedAfter;

    /// <summary>The keys of the sources being recorded now, those between attempts included.</summary>
    public IReadOnlyCollection<string> ActiveKeys => [.._entries.Keys];

    /// <summary>Starts recording <paramref name="source"/>. The first recorder, context and stamper are built here, so
    /// a factory that throws throws from this call and adds nothing; a later attempt's factory that throws is a failed
    /// attempt, logged to the first context's log.</summary>
    /// <returns>False when its key is already being recorded, or this host is stopping.</returns>
    public bool TryAdd(RecordingSource source) {
        var entry = new Entry(source);

        lock (_admission) {
            if (_stopping || !_entries.TryAdd(source.Key, entry)) {
                entry.Dispose();

                return false;
            }
        }

        RecorderContext? built = null;

        try {
            var attempt = new RecorderAttempt(_time);
            var context = _contexts(source, attempt.Beat);
            built = context;

            var recorder = Create(source, entry, context, context, attempt);
            entry.First = context;
            _ = Task.Run(() => RunAsync(source, entry, context, recorder, attempt));
        } catch {
            Remove(source, entry, built);

            throw;
        }

        return true;
    }

    /// <summary>Finishes every source within <paramref name="bound"/>, for a host going away, and adds no more. A drain
    /// still sending when the bound nears is cut off, and its recorder spools what it had left. A source still going at
    /// the bound is stuck where no token reaches, so it is marked for import and left behind, in
    /// <see cref="ActiveKeys"/> until the host gives it up. Claims stay, because only the owner knows whether it is coming
    /// back.</summary>
    /// <param name="ct">Brings the bound forward, for an owner out of time: what hasn't finished is cut and marked at
    /// once, and this returns rather than throws.</param>
    public async Task StopAllAsync(TimeSpan bound, CancellationToken ct) {
        Entry[] entries;

        lock (_admission) {
            _stopping = true;
            entries   = [.._entries.Values];
        }

        var started = _time.GetTimestamp();
        var ended   = Task.WhenAll(entries.Select(entry => entry.Done));
        var reserve = bound / 2 < SpoolReserve ? bound / 2 : SpoolReserve;

        foreach (var entry in entries) entry.Hurry();

        if (await EndsWithinAsync(ended, bound - reserve, ct)) return;

        foreach (var entry in entries) entry.CutSends();

        if (await EndsWithinAsync(ended, bound - _time.GetElapsedTime(started), ct)) return;

        foreach (var entry in entries) {
            if (entry.Done.IsCompleted || entry.First is not { } first) continue;

            Log(first, $"Recording {entry.Source.Key} did not finish within the shutdown's bound, marking it for import");
            MarkNeedsImport(entry, first, "shutdown bound passed before the final drain finished");
        }
    }

    async Task<bool> EndsWithinAsync(Task ended, TimeSpan limit, CancellationToken ct) {
        try {
            await ended.WaitAsync(limit > TimeSpan.Zero ? limit : TimeSpan.Zero, _time, ct);

            return true;
        } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) {
            return false;
        }
    }

    // A running recorder hears a stop itself. Between attempts none is listening, and the server's drain wait is
    // shorter than a backoff.
    Task OnStopRequestedAsync(string key, string reason) {
        if (_entries.TryGetValue(key, out var entry)) entry.Hurry();

        return Task.CompletedTask;
    }

    /// <summary>Stops the source <paramref name="key"/> names, makes its final drain, and returns once it has left
    /// <see cref="ActiveKeys"/>. A key nothing records is already finished. <paramref name="ct"/> bounds this wait
    /// only: cancelling it throws here and leaves the drain running.</summary>
    /// <param name="hurried">Whoever asks won't wait long, so the drain doesn't either, as for a server stop.</param>
    public async Task FinishAsync(string key, CancellationToken ct, bool hurried = false) {
        if (!_entries.TryGetValue(key, out var entry)) return;

        if (hurried) entry.Hurry();
        else entry.CancelRun();

        await entry.Done.WaitAsync(ct);
    }

    /// <summary>Ends the source <paramref name="key"/> names where it stands, for a person who wants its data gone: no
    /// final drain, and one already running stops rather than finishes, with nothing spooled, marked or handed on.</summary>
    /// <returns>Completes once it has left <see cref="ActiveKeys"/>, which a source stuck where no token reaches does only
    /// once the host gives it up. It ends the same way if it wakes.</returns>
    public Task AbandonAsync(string key) {
        if (!_entries.TryGetValue(key, out var entry)) return Task.CompletedTask;

        entry.Abandon();

        return entry.Done;
    }

    // A log or beat that throws, as the daemon's file log can, would otherwise fail the recorder at its first line.
    TranscriptRecorder Create(RecordingSource source, Entry entry, RecorderContext first, RecorderContext context, RecorderAttempt attempt) {
        attempt.BeatsWith(Beats(source, context.Beat));

        return new(source, _transport, _stampers(source), _time, context with { Log = Guarded(context.Log), Beat = attempt.Beat }, _claims) {
            OpenTranscript          = _openTranscript,
            ConnectAbandoned        = connect => _ = DetachOnceLandedAsync(source, connect, first),
            FailsOnLostRegistration = true,
            Abandoned               = () => entry.Abandoned || attempt.Superseded,
        };
    }

    // Guarded apart, because the heartbeat file and the claim fail apart: a backoff has to keep beating a claim kept
    // offline, or a hook would take a source this host is still retrying.
    Action Beats(RecordingSource source, Action beat) {
        var heartbeat = Guarded(beat);

        if (_claims is not { } claims) return heartbeat;

        var offline = Guarded(() => _keepsClaimOffline?.Invoke(source) ?? true, fallback: true);
        var claim   = Guarded(() => {
            if (offline || _transport.IsConnected) claims.Beat(source.Key);
        });

        return () => {
            heartbeat();
            claim();
        };
    }

    internal static Action<string> Guarded(Action<string> log) => message => {
        try {
            log(message);
        } catch {
            // Nowhere left to report it.
        }
    };

    static bool Guarded(Func<bool> read, bool fallback) {
        try {
            return read();
        } catch {
            return fallback;
        }
    }

    internal static Action Guarded(Action beat) => () => {
        try {
            beat();
        } catch {
            // A failing beat only makes the source look stale.
        }
    };

    // Never throws, and always leaves the dictionary, whatever a recorder did.
    async Task RunAsync(RecordingSource source, Entry entry, RecorderContext first, TranscriptRecorder? recorder, RecorderAttempt attempt) {
        var key     = source.Key;
        var delay   = FirstRetryDelay;
        var token   = entry.Token;
        var context = first;

        try {
            while (true) {
                var started   = _time.GetTimestamp();
                var ran       = TimeSpan.Zero;
                var connected = false;
                var stopped   = token.IsCancellationRequested;

                if (recorder is not null) {
                    Exception? failure = null;
                    var        ended   = false;

                    try {
                        var run = Task.Run(() => recorder.RunAsync(token), CancellationToken.None);

                        if (!await EndsBeatingAsync(run, attempt)) {
                            // Read ahead of the supersession, which stops the recorder too.
                            stopped   = token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested;
                            connected = recorder.Connected;
                            ran       = _time.GetElapsedTime(started, attempt.BeatAt);
                            ended     = entry.Abandoned;
                            failure   = Supersede(source, first, recorder, attempt, run, ended ? "it ends here" : stopped ? "it drains once" : "another attempt takes over");

                            if (ended) return;
                        } else {
                            try {
                                await run;
                                connected = recorder.Connected;
                            } catch (OperationCanceledException) when (token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested) {
                                // Stopped before it connected: the watcher leaves that undrained, the host drains it.
                            } catch (Exception ex) {
                                failure   = ex;
                                connected = recorder.Connected;
                            }

                            ran     = _time.GetElapsedTime(started);
                            stopped = token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested;

                            if (failure is not null) {
                                var next = recorder.LostClaim ? "lost its claim, so it ends here" : stopped ? "is stopping, so it drains once" : "will retry";
                                Log(first, $"Recording {key} failed and {next}: {failure.Message}");
                            }

                            if (recorder.LostClaim) {
                                ended = true;

                                return;
                            }

                            if (failure is null && connected) {
                                ended   = true;
                                failure = await FinishRecorderAsync(source, entry, recorder, attempt, first);

                                return;
                            }
                        }
                    } finally {
                        if (!attempt.Superseded) recorder.Dispose();
                        ReportAttempt(source, entry, first, recorder, context, ended, failure, attempt.Superseded);
                    }
                }

                // Nothing was finished: the run failed, was stopped before it connected, or was given up on.
                if (stopped) {
                    await DrainOnceAsync(source, entry, first);

                    return;
                }

                // A run given up on counts only up to its last beat: being stuck earns no quick return.
                if (connected && ran >= MaxRetryDelay) delay = FirstRetryDelay;

                if (!await DelayWithBeatsAsync(delay, Beats(source, first.Beat), token)) {
                    await DrainOnceAsync(source, entry, first);

                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));

                try {
                    attempt  = new RecorderAttempt(_time);
                    context  = _contexts(source, attempt.Beat);
                    recorder = Create(source, entry, first, context, attempt);
                } catch (Exception ex) {
                    Log(first, $"Recording {key} could not prepare an attempt, will retry: {ex.Message}");
                    recorder = null;
                    ReportAttempt(source, entry, first, null, null, false, ex);
                }
            }
        } finally {
            Remove(source, entry, first);
        }
    }

    // The backoff outlasts the heartbeat's staleness threshold, so one plain delay would make a healthy source look wedged.
    async Task<bool> DelayWithBeatsAsync(TimeSpan total, Action beat, CancellationToken ct) {
        foreach (var slice in TranscriptRecorder.HeartbeatSlices(total, TranscriptRecorder.HeartbeatSlice)) {
            beat();

            try {
                await Task.Delay(slice, _time, ct);
            } catch (OperationCanceledException) {
                return false;
            }
        }

        return true;
    }

    async Task<Exception?> FinishRecorderAsync(RecordingSource source, Entry entry, TranscriptRecorder recorder, RecorderAttempt attempt, RecorderContext first) {
        try {
            if (entry.Hurried) recorder.RequestStop();

            var drain = Task.Run(() => recorder.FinishAsync(entry.DrainToken), CancellationToken.None);
            if (!await EndsBeatingAsync(drain, attempt)) return GiveUpDrain(source, entry, first, recorder, attempt, drain);

            await drain;

            return null;
        } catch (Exception ex) {
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");
            MarkNeedsImport(entry, first, $"final drain did not complete: {ex.Message}");

            return ex;
        }
    }

    // One fresh recorder connects and drains, for a source whose run never connected. If that doesn't complete, the
    // import recovers the lines; they are never dropped.
    async Task DrainOnceAsync(RecordingSource source, Entry entry, RecorderContext first) {
        TranscriptRecorder? recorder = null;
        RecorderContext?    context  = null;
        RecorderAttempt?    attempt  = null;
        Exception?          failure  = null;

        try {
            attempt  = new RecorderAttempt(_time);
            context  = _contexts(source, attempt.Beat);
            recorder = Create(source, entry, first, context, attempt);
            if (entry.Hurried) recorder.RequestStop();

            var drain = Task.Run(() => recorder.DrainOnceAsync(entry.DrainToken), CancellationToken.None);

            if (await EndsBeatingAsync(drain, attempt)) await drain;
            else failure = GiveUpDrain(source, entry, first, recorder, attempt, drain);
        } catch (Exception ex) {
            failure = ex;
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");

            MarkNeedsImport(entry, first, $"final drain did not complete: {ex.Message}");
        } finally {
            var superseded = attempt?.Superseded == true;

            if (!superseded) recorder?.Dispose();
            ReportAttempt(source, entry, first, recorder, context, true, failure, superseded);
        }
    }

    // A transport that is down retries, and a source waiting on it has the whole threshold again once it is back. Callers
    // start the work off their own thread: a call that never returns before its first await would never reach the watch.
    async Task<bool> EndsBeatingAsync(Task work, RecorderAttempt attempt) {
        while (!await attempt.EndsBeatingAsync(work, Watchdog)) {
            if (_transport.IsConnected) return false;

            attempt.Excuse();
        }

        return true;
    }

    // Whatever the attempt does if it wakes is refused, its tokens are cancelled anyway, and its recorder is disposed by
    // its own work's end, if that ever comes: disposing it under that work would fault it.
    static Exception Supersede(RecordingSource source, RecorderContext first, TranscriptRecorder recorder, RecorderAttempt attempt, Task work, string next) {
        var wedge = new TimeoutException($"Recording {source.Key} went {attempt.SinceBeat.TotalSeconds:0}s without a beat");

        attempt.Supersede();
        recorder.Supersede();

        _ = work.ContinueWith(static (ended, state) => {
            _ = ended.Exception;

            try {
                ((TranscriptRecorder)state!).Dispose();
            } catch {
                // Nobody is left to hear of it.
            }
        }, recorder, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        Log(first, $"{wedge.Message}, so {next}");

        return wedge;
    }

    // A second drain could race the stuck one, so the import recovers what it had left.
    Exception GiveUpDrain(RecordingSource source, Entry entry, RecorderContext first, TranscriptRecorder recorder, RecorderAttempt attempt, Task drain) {
        var wedge = Supersede(source, first, recorder, attempt, drain, entry.Abandoned ? "it ends here" : "it is marked for import and ends");

        MarkNeedsImport(entry, first, $"final drain stuck: {wedge.Message}");

        return wedge;
    }

    // However the connect ended, nothing of it is left to land. A recorder holding the key shares the registration, and
    // the server's detach goes by key and connection, so it stays until that recorder's own end, which follows this.
    async Task DetachOnceLandedAsync(RecordingSource source, Task connect, RecorderContext first) {
        try {
            await connect;
        } catch {
            // Its outcome changes nothing here.
        }

        if (!await DetachAsync(source, first, () => !_entries.TryGetValue(source.Key, out var holder) || holder.Detaching))
            Log(first, $"A late connect of {source.Key} landed while it is being recorded, so it stays registered");
    }

    // Never throws, or a broken owner would stop a retry. Retires the ack count where a watcher would, once no attempt
    // follows. A lost claim leaves it: the hook that took the source has a recorder of its own, and the count is its.
    // What a superseded recorder says of itself can be the supersession's doing, so none of it is taken.
    void ReportAttempt(
            RecordingSource source, Entry entry, RecorderContext first, TranscriptRecorder? recorder, RecorderContext? context, bool sourceEnded, Exception? failure,
            bool superseded = false) {
        try {
            var heard = superseded ? null : recorder;

            if (sourceEnded && recorder is not null && !(superseded ? entry.Abandoned : recorder.LostClaim)) recorder.RetireAckCursor();

            _attemptEnded?.Invoke(new RecorderAttemptEnded(
                source, sourceEnded, entry.Abandoned || heard?.LostClaim == true, heard?.EndedIdle == true, context is null ? [] : [..context.Housekeeping.SpawnedChildKeys], failure,
                heard?.LeftLines == true));
        } catch (Exception ex) {
            Log(first, $"Telling the owner how an attempt at {source.Key} ended failed: {ex.Message}");
        }
    }

    // Started under the lock that admits a key, so a recorder added after this connects behind it. The catch stays
    // outside that lock: a transport can throw before its first await, and a log can block.
    async Task<bool> DetachAsync(RecordingSource source, RecorderContext? first, Func<bool>? due = null) {
        try {
            using var budget = new CancellationTokenSource(DetachBudget, _time);

            Task detach;

            lock (_admission) {
                if (due?.Invoke() == false) return false;

                detach = _transport.DetachAsync(source, budget.Token);
            }

            await detach;
        } catch (Exception ex) {
            Log(first, $"Detaching {source.Key} failed: {ex.Message}");
        }

        return true;
    }

    void MarkNeedsImport(Entry entry, RecorderContext first, string reason) {
        if (entry.Abandoned) return;

        var source = entry.Source;

        try {
            var spool = new TranscriptSpool(first.Server.Config, _time, first.Server.Url) { FailureLog = message => Log(first, message) };

            if (!spool.MarkNeedsImport(source.SessionId, reason)) {
                Log(first, $"Could not mark {source.Key} for import: the marker was not written");
            }
        } catch (Exception ex) {
            Log(first, $"Could not mark {source.Key} for import: {ex.Message}");
        }
    }

    // A failing logger must not turn one source's trouble into a leak or into another source's problem.
    static void Log(RecorderContext? context, string message) {
        try {
            context?.Log(message);
        } catch {
            // Nowhere left to report it.
        }
    }

    // The detach starts while the key is still refused and is left to run, so a hang can't hold up whoever waits for the
    // end. Every way out takes it, an add that failed included: a late connect may have left its registration to this entry.
    void Remove(RecordingSource source, Entry entry, RecorderContext? first) {
        entry.BeginDetach();
        _ = DetachAsync(source, first);
        _entries.TryRemove(new KeyValuePair<string, Entry>(source.Key, entry));
        entry.Complete();
    }

    sealed class Entry : IDisposable {
        readonly CancellationTokenSource _run   = new();
        readonly CancellationTokenSource _drain = new();
        readonly TaskCompletionSource    _done  = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool _hurried;
        bool _detaching;
        bool _abandoned;

        public RecordingSource Source { get; }

        /// <summary>The first attempt's context, which outlives the attempts for logging. Null until it is built.</summary>
        public RecorderContext? First { get; set; }

        /// <summary>Cancelled when the source is asked to finish.</summary>
        public CancellationToken Token { get; }

        /// <summary>Cancelled only by a shutdown out of time: a final drain otherwise runs for as long as it needs.</summary>
        public CancellationToken DrainToken { get; }

        /// <summary>Whoever stopped this source will not wait long, so its final drain must not either.</summary>
        public bool Hurried => Volatile.Read(ref _hurried);

        /// <summary>This source is over where it stands, for good.</summary>
        public bool Abandoned => Volatile.Read(ref _abandoned);

        /// <summary>This source has ended and its detach is on its way, so a connect landing behind it needs its own.</summary>
        public bool Detaching => Volatile.Read(ref _detaching);

        public Task Done => _done.Task;

        public Entry(RecordingSource source) {
            Source     = source;
            Token      = _run.Token;
            DrainToken = _drain.Token;
        }

        public void BeginDetach() => Volatile.Write(ref _detaching, true);

        public void CancelRun() {
            try {
                _run.Cancel();
            } catch (ObjectDisposedException) {
                // The source has ended.
            }
        }

        /// <summary>Cancels off the caller's thread: that one is a transport's dispatch or a shutdown's loop over every
        /// source, and a plain cancel would run this source's drain on it, up to the drain's first real wait.</summary>
        public void Hurry() {
            Volatile.Write(ref _hurried, true);
            _run.CancelOffThread();
        }

        /// <summary>Off the caller's thread, for the reason <see cref="Hurry"/> is.</summary>
        public void CutSends() => _drain.CancelOffThread();

        /// <summary>Ends the run and a final drain alike, off the caller's thread for the reason <see cref="Hurry"/> is.</summary>
        public void Abandon() {
            Volatile.Write(ref _abandoned, true);
            _run.CancelOffThread();
            _drain.CancelOffThread();
        }

        public void Complete() {
            _done.TrySetResult();
            Dispose();
        }

        public void Dispose() {
            _run.Dispose();
            _drain.Dispose();
        }
    }
}
