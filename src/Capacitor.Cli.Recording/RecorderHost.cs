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
/// A server stop ends a backoff as a finish does, and a final drain after one is in a hurry.
/// </summary>
public sealed class RecorderHost {
    static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxRetryDelay   = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DetachBudget    = TimeSpan.FromSeconds(5);

    // Kept back from a shutdown's sends, at most half its bound: the tail's read, and redaction, whose floor is a second.
    static readonly TimeSpan SpoolReserve = TimeSpan.FromSeconds(2);

    readonly IRecorderTransport _transport;
    readonly Func<RecordingSource, IRowStamper> _stampers;
    readonly Func<RecordingSource, RecorderContext> _contexts;
    readonly TimeProvider _time;
    readonly Func<string, FileStream>? _openTranscript;
    readonly RecorderClaims? _claims;
    readonly Action<RecorderAttemptEnded>? _attemptEnded;
    readonly ConcurrentDictionary<string, Entry> _entries = new();
    readonly Lock _admission = new();
    bool _stopping;

    public RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, RecorderContext> contexts, TimeProvider time,
        RecorderClaims? claims = null, Action<RecorderAttemptEnded>? attemptEnded = null)
        : this(transport, stampers, contexts, time, null, claims, attemptEnded) { }

    internal RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, RecorderContext> contexts, TimeProvider time,
        Func<string, FileStream>? openTranscript, RecorderClaims? claims = null, Action<RecorderAttemptEnded>? attemptEnded = null) {
        _transport      = transport;
        _stampers       = stampers;
        _contexts       = contexts;
        _time           = time;
        _openTranscript = openTranscript;
        _claims         = claims;
        _attemptEnded   = attemptEnded;

        _transport.StopRequested += OnStopRequestedAsync;
    }

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

        try {
            var context  = _contexts(source);
            var recorder = Create(source, context);
            entry.First = context;
            _ = Task.Run(() => RunAsync(source, entry, context, recorder));
        } catch {
            Remove(source.Key, entry);

            throw;
        }

        return true;
    }

    /// <summary>Finishes every source within <paramref name="bound"/>, for a host going away, and adds no more. A drain
    /// still sending when the bound nears is cut off, and its recorder spools what it had left. A source still going at
    /// the bound is stuck where no token reaches, so it is marked for import and left behind, in
    /// <see cref="ActiveKeys"/>. Claims stay, because only the owner knows whether it is coming back.</summary>
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
            MarkNeedsImport(entry.Source, first, "shutdown bound passed before the final drain finished");
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
    public async Task FinishAsync(string key, CancellationToken ct) {
        if (!_entries.TryGetValue(key, out var entry)) return;

        entry.CancelRun();

        await entry.Done.WaitAsync(ct);
    }

    // A log or beat that throws, as the daemon's file log can, would otherwise fail the recorder at its first line.
    TranscriptRecorder Create(RecordingSource source, RecorderContext context) =>
        new(source, _transport, _stampers(source), _time, context with { Log = Guarded(context.Log), Beat = Beats(source, context.Beat) }, _claims) { OpenTranscript = _openTranscript };

    // Guarded apart, because the heartbeat file and the claim fail apart: a backoff has to keep beating the claim, or a
    // hook would take a source this host is still retrying.
    Action Beats(RecordingSource source, Action beat) {
        var heartbeat = Guarded(beat);

        if (_claims is not { } claims) return heartbeat;

        var claim = Guarded(() => claims.Beat(source.Key));

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

    internal static Action Guarded(Action beat) => () => {
        try {
            beat();
        } catch {
            // A failing beat only makes the source look stale.
        }
    };

    // Never throws, and always leaves the dictionary, whatever a recorder did.
    async Task RunAsync(RecordingSource source, Entry entry, RecorderContext first, TranscriptRecorder? recorder) {
        var key     = source.Key;
        var delay   = FirstRetryDelay;
        var token   = entry.Token;
        var context = first;

        try {
            while (true) {
                var started   = _time.GetTimestamp();
                var connected = false;
                var stopped   = token.IsCancellationRequested;

                if (recorder is not null) {
                    Exception? failure = null;
                    var        ended   = false;

                    try {
                        try {
                            await recorder.RunAsync(token);
                            connected = recorder.Connected;
                        } catch (OperationCanceledException) when (token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested) {
                            // Stopped before it connected: the watcher leaves that undrained, the host drains it.
                        } catch (Exception ex) {
                            failure   = ex;
                            connected = recorder.Connected;
                        }

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
                            failure = await FinishRecorderAsync(source, entry, recorder, first);

                            return;
                        }
                    } finally {
                        recorder.Dispose();
                        ReportAttempt(source, first, recorder, context, ended, failure);
                    }
                }

                // Nothing was finished: the run failed, or was stopped before it connected.
                if (stopped) {
                    await DrainOnceAsync(source, entry, first);

                    return;
                }

                if (connected && _time.GetElapsedTime(started) >= MaxRetryDelay) delay = FirstRetryDelay;

                if (!await DelayWithBeatsAsync(delay, Beats(source, first.Beat), token)) {
                    await DrainOnceAsync(source, entry, first);

                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));

                try {
                    context  = _contexts(source);
                    recorder = Create(source, context);
                } catch (Exception ex) {
                    Log(first, $"Recording {key} could not prepare an attempt, will retry: {ex.Message}");
                    recorder = null;
                    ReportAttempt(source, first, null, null, false, ex);
                }
            }
        } finally {
            // Started while the key is still refused and left to run, so a hang can't hold up whoever waits for the end.
            _ = DetachAsync(source, first);
            Remove(key, entry);
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

    async Task<Exception?> FinishRecorderAsync(RecordingSource source, Entry entry, TranscriptRecorder recorder, RecorderContext first) {
        try {
            if (entry.Hurried) recorder.RequestStop();

            await recorder.FinishAsync(entry.DrainToken);

            return null;
        } catch (Exception ex) {
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");
            MarkNeedsImport(source, first, $"final drain did not complete: {ex.Message}");

            return ex;
        }
    }

    // One fresh recorder connects and drains, for a source whose run never connected. If that doesn't complete, the
    // import recovers the lines; they are never dropped.
    async Task DrainOnceAsync(RecordingSource source, Entry entry, RecorderContext first) {
        TranscriptRecorder? recorder = null;
        RecorderContext?    context  = null;
        Exception?          failure  = null;

        try {
            context  = _contexts(source);
            recorder = Create(source, context);
            if (entry.Hurried) recorder.RequestStop();

            await recorder.DrainOnceAsync(entry.DrainToken);
        } catch (Exception ex) {
            failure = ex;
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");

            MarkNeedsImport(source, first, $"final drain did not complete: {ex.Message}");
        } finally {
            if (recorder?.AbandonedConnect is { } connect) _ = DetachOnceLandedAsync(source, entry, connect, first);

            recorder?.Dispose();
            ReportAttempt(source, first, recorder, context, true, failure);
        }
    }

    // However the connect ended: a cancelled invocation can already be on the wire. A recorder that took the key back
    // shares the registration, and the server's detach goes by key and connection, so it stays. Started under the lock
    // that admits a key, so any recorder added after this look connects behind the detach.
    async Task DetachOnceLandedAsync(RecordingSource source, Entry entry, Task connect, RecorderContext first) {
        try {
            await connect;
        } catch {
            // Its outcome changes nothing here.
        }

        Task? detach = null;

        lock (_admission) {
            if (!_entries.TryGetValue(source.Key, out var holder) || holder == entry) detach = DetachAsync(source, first);
        }

        if (detach is not null) await detach;
        else Log(first, $"A late connect of {source.Key} landed, and it is being recorded again, so it stays registered");
    }

    // Never throws, or a broken owner would stop a retry. Retires the ack count where a watcher would, once no attempt
    // follows. A lost claim leaves it: the hook that took the source has a recorder of its own, and the count is its.
    void ReportAttempt(RecordingSource source, RecorderContext first, TranscriptRecorder? recorder, RecorderContext? context, bool sourceEnded, Exception? failure) {
        try {
            if (sourceEnded && recorder is { LostClaim: false }) recorder.RetireAckCursor();

            _attemptEnded?.Invoke(new RecorderAttemptEnded(
                source, sourceEnded, recorder?.LostClaim == true, recorder?.EndedIdle == true, context is null ? [] : [..context.Housekeeping.SpawnedChildKeys], failure));
        } catch (Exception ex) {
            Log(first, $"Telling the owner how an attempt at {source.Key} ended failed: {ex.Message}");
        }
    }

    async Task DetachAsync(RecordingSource source, RecorderContext first) {
        try {
            using var budget = new CancellationTokenSource(DetachBudget, _time);

            await _transport.DetachAsync(source, budget.Token);
        } catch (Exception ex) {
            Log(first, $"Detaching {source.Key} failed: {ex.Message}");
        }
    }

    void MarkNeedsImport(RecordingSource source, RecorderContext first, string reason) {
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
    static void Log(RecorderContext context, string message) {
        try {
            context.Log(message);
        } catch {
            // Nowhere left to report it.
        }
    }

    void Remove(string key, Entry entry) {
        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        entry.Complete();
    }

    sealed class Entry : IDisposable {
        readonly CancellationTokenSource _run   = new();
        readonly CancellationTokenSource _drain = new();
        readonly TaskCompletionSource    _done  = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool _hurried;

        public RecordingSource Source { get; }

        /// <summary>The first attempt's context, which outlives the attempts for logging. Null until it is built.</summary>
        public RecorderContext? First { get; set; }

        /// <summary>Cancelled when the source is asked to finish.</summary>
        public CancellationToken Token { get; }

        /// <summary>Cancelled only by a shutdown out of time: a final drain otherwise runs for as long as it needs.</summary>
        public CancellationToken DrainToken { get; }

        /// <summary>Whoever stopped this source will not wait long, so its final drain must not either.</summary>
        public bool Hurried => Volatile.Read(ref _hurried);

        public Task Done => _done.Task;

        public Entry(RecordingSource source) {
            Source     = source;
            Token      = _run.Token;
            DrainToken = _drain.Token;
        }

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

            try {
                _ = _run.CancelAsync();
            } catch (ObjectDisposedException) {
                // The source has ended.
            }
        }

        /// <summary>Off the caller's thread, for the reason <see cref="Hurry"/> is.</summary>
        public void CutSends() {
            try {
                _ = _drain.CancelAsync();
            } catch (ObjectDisposedException) {
                // The source has ended.
            }
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
