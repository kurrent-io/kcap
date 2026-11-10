using System.Collections.Concurrent;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Records many sources in one process over one transport, one loop per source. Nothing outside the daemon
/// respawns a source that fails here, so the host retries it: the key stays in <see cref="ActiveKeys"/> and
/// <see cref="TryAdd"/> stays refused, and each attempt is a fresh recorder, context and stamper that resume
/// from the server's cursor. Retries wait 1s doubling to 30s and have no ceiling, because a source ends when its
/// owner calls <see cref="FinishAsync"/>. A recorder that returns cleanly ends its source and is never retried.
/// </summary>
public sealed class RecorderHost {
    static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxRetryDelay   = TimeSpan.FromSeconds(30);

    readonly IRecorderTransport _transport;
    readonly Func<RecordingSource, IRowStamper> _stampers;
    readonly Func<RecordingSource, RecorderContext> _contexts;
    readonly TimeProvider _time;
    readonly Func<string, FileStream>? _openTranscript;
    readonly ConcurrentDictionary<string, Entry> _entries = new();

    public RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, RecorderContext> contexts, TimeProvider time)
        : this(transport, stampers, contexts, time, null) { }

    internal RecorderHost(
        IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, RecorderContext> contexts, TimeProvider time,
        Func<string, FileStream>? openTranscript) {
        _transport      = transport;
        _stampers       = stampers;
        _contexts       = contexts;
        _time           = time;
        _openTranscript = openTranscript;
    }

    /// <summary>The keys of the sources being recorded now, those between attempts included.</summary>
    public IReadOnlyCollection<string> ActiveKeys => [.._entries.Keys];

    /// <summary>Starts recording <paramref name="source"/>. The first recorder, context and stamper are built here, so
    /// a factory that throws throws from this call and adds nothing; a later attempt's factory that throws is a failed
    /// attempt, logged to the first context's log.</summary>
    /// <returns>False when its key is already being recorded.</returns>
    public bool TryAdd(RecordingSource source) {
        var entry = new Entry();
        if (!_entries.TryAdd(source.Key, entry)) {
            entry.Dispose();

            return false;
        }

        try {
            var context  = _contexts(source);
            var recorder = Create(source, context);
            _ = Task.Run(() => RunAsync(source, entry, context, recorder));
        } catch {
            Remove(source.Key, entry);

            throw;
        }

        return true;
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
        new(source, _transport, _stampers(source), _time, context with { Log = Guarded(context.Log), Beat = Guarded(context.Beat) }) { OpenTranscript = _openTranscript };

    static Action<string> Guarded(Action<string> log) => message => {
        try {
            log(message);
        } catch {
            // Nowhere left to report it.
        }
    };

    static Action Guarded(Action beat) => () => {
        try {
            beat();
        } catch {
            // A failing beat only makes the source look stale.
        }
    };

    // Never throws, and always leaves the dictionary, whatever a recorder did.
    async Task RunAsync(RecordingSource source, Entry entry, RecorderContext first, TranscriptRecorder? recorder) {
        var key   = source.Key;
        var delay = FirstRetryDelay;
        var token = entry.Token;

        try {
            while (true) {
                var started   = _time.GetTimestamp();
                var connected = false;
                var stopped   = token.IsCancellationRequested;

                if (recorder is not null) {
                    try {
                        var failed = false;

                        try {
                            await recorder.RunAsync(token);
                            connected = recorder.Connected;
                        } catch (OperationCanceledException) when (token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested) {
                            // Stopped before it connected: the watcher leaves that undrained, the host drains it.
                        } catch (Exception ex) {
                            failed    = true;
                            connected = recorder.Connected;
                            Log(first, $"Recording {key} failed, will retry: {ex.Message}");
                        }

                        stopped = token.IsCancellationRequested || recorder.Stopping.IsCancellationRequested;

                        if (!failed && connected) {
                            await FinishRecorderAsync(source, recorder, first);

                            return;
                        }
                    } finally {
                        recorder.Dispose();
                    }
                }

                // Nothing was finished: the run failed, or was stopped before it connected.
                if (stopped) {
                    await DrainOnceAsync(source, first);

                    return;
                }

                if (connected && _time.GetElapsedTime(started) >= MaxRetryDelay) delay = FirstRetryDelay;

                if (!await DelayWithBeatsAsync(delay, first, token)) {
                    await DrainOnceAsync(source, first);

                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));

                try {
                    recorder = Create(source, _contexts(source));
                } catch (Exception ex) {
                    Log(first, $"Recording {key} could not prepare an attempt, will retry: {ex.Message}");
                    recorder = null;
                }
            }
        } finally {
            Remove(key, entry);
        }
    }

    // The backoff outlasts the heartbeat's staleness threshold, so one plain delay would make a healthy source look wedged.
    async Task<bool> DelayWithBeatsAsync(TimeSpan total, RecorderContext first, CancellationToken ct) {
        foreach (var slice in TranscriptRecorder.HeartbeatSlices(total, TranscriptRecorder.HeartbeatSlice)) {
            try {
                first.Beat();
            } catch {
                // A failing beat must not end the retry.
            }

            try {
                await Task.Delay(slice, _time, ct);
            } catch (OperationCanceledException) {
                return false;
            }
        }

        return true;
    }

    async Task FinishRecorderAsync(RecordingSource source, TranscriptRecorder recorder, RecorderContext first) {
        try {
            await recorder.FinishAsync(CancellationToken.None);
        } catch (Exception ex) {
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");
            MarkNeedsImport(source, first, $"final drain did not complete: {ex.Message}");
        }
    }

    // One fresh recorder connects and drains, for a source whose run never connected. If that doesn't complete, the
    // import recovers the lines; they are never dropped.
    async Task DrainOnceAsync(RecordingSource source, RecorderContext first) {
        TranscriptRecorder? recorder = null;

        try {
            var context = _contexts(source);
            recorder = Create(source, context);
            await recorder.DrainOnceAsync(CancellationToken.None);
        } catch (Exception ex) {
            Log(first, $"Final drain of {source.Key} failed, marking it for import: {ex.Message}");

            MarkNeedsImport(source, first, $"final drain did not complete: {ex.Message}");
        } finally {
            recorder?.Dispose();
        }
    }

    void MarkNeedsImport(RecordingSource source, RecorderContext first, string reason) {
        try {
            if (!new TranscriptSpool(first.Server.Config, _time, first.Server.Url).MarkNeedsImport(source.SessionId, reason)) {
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
        readonly CancellationTokenSource _run  = new();
        readonly TaskCompletionSource    _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cancelled when the source is asked to finish.</summary>
        public CancellationToken Token { get; }

        public Task Done => _done.Task;

        public Entry() => Token = _run.Token;

        public void CancelRun() {
            try {
                _run.Cancel();
            } catch (ObjectDisposedException) {
                // The source has ended.
            }
        }

        public void Complete() {
            _done.TrySetResult();
            Dispose();
        }

        public void Dispose() => _run.Dispose();
    }
}
