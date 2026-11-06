using System.Collections.Concurrent;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Records many sources in one process over one transport, one <see cref="TranscriptRecorder"/> and one loop per
/// source. A source's failure ends that source only: it is logged to that source's own log and the source leaves
/// <see cref="ActiveKeys"/>, so adding it again resumes from the server's cursor, as a respawned watcher does.
/// </summary>
/// <param name="openTranscript">How a recorder opens its transcript for a poll; null opens the file.</param>
public sealed class RecorderHost(
        IRecorderTransport transport,
        Func<RecordingSource, IRowStamper> stampers,
        Func<RecordingSource, RecorderContext> contexts,
        TimeProvider time,
        Func<string, FileStream>? openTranscript = null) {
    readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>The keys of the sources being recorded now.</summary>
    public IReadOnlyCollection<string> ActiveKeys => [.._entries.Keys];

    /// <summary>Starts recording <paramref name="source"/>.</summary>
    /// <returns>False when its key is already being recorded.</returns>
    public bool TryAdd(RecordingSource source) {
        var entry = new Entry();
        if (!_entries.TryAdd(source.Key, entry)) {
            entry.Dispose();

            return false;
        }

        TranscriptRecorder? recorder = null;

        try {
            var context = contexts(source);
            recorder = new TranscriptRecorder(source, transport, stampers(source), time, context, openTranscript);
            _ = Task.Run(() => RunAsync(source.Key, entry, recorder, context.Log));
        } catch {
            recorder?.Dispose();
            Remove(source.Key, entry);

            throw;
        }

        return true;
    }

    /// <summary>Stops the source <paramref name="key"/> names, makes its final drain, and returns once it has left
    /// <see cref="ActiveKeys"/>. A key nothing records is already finished. Cancelling <paramref name="ct"/> cuts
    /// the drain short and throws; the source still ends.</summary>
    public async Task FinishAsync(string key, CancellationToken ct) {
        if (!_entries.TryGetValue(key, out var entry)) return;

        await using var cancelFinish = ct.Register(static state => ((Entry)state!).CancelFinish(), entry).ConfigureAwait(false);
        entry.CancelRun();

        await entry.Done.WaitAsync(ct);
    }

    // The loop never throws and always leaves the dictionary, whatever the recorder did.
    async Task RunAsync(string key, Entry entry, TranscriptRecorder recorder, Action<string> log) {
        try {
            try {
                await recorder.RunAsync(entry.RunToken);
            } catch (OperationCanceledException) when (recorder.Stopping.IsCancellationRequested) {
                // Stopped before it connected, which the watcher also leaves undrained.
                return;
            }

            await recorder.FinishAsync(entry.FinishToken);
        } catch (OperationCanceledException) when (entry.FinishToken.IsCancellationRequested) {
            Log(log, $"Final drain of {key} was cut short");
        } catch (Exception ex) {
            Log(log, $"Recording {key} failed: {ex.Message}");
        } finally {
            Remove(key, entry);
            recorder.Dispose();
        }
    }

    // A failing logger must not turn one source's end into a leak or into another source's problem.
    static void Log(Action<string> log, string message) {
        try {
            log(message);
        } catch {
            // Nowhere left to report it.
        }
    }

    void Remove(string key, Entry entry) {
        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        entry.Complete();
    }

    sealed class Entry : IDisposable {
        readonly CancellationTokenSource _run    = new();
        readonly CancellationTokenSource _finish = new();
        readonly TaskCompletionSource    _done   = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken RunToken    => _run.Token;
        public CancellationToken FinishToken => _finish.Token;
        public Task              Done        => _done.Task;

        public void CancelRun() => Cancel(_run);

        public void CancelFinish() => Cancel(_finish);

        public void Complete() {
            _done.TrySetResult();
            Dispose();
        }

        public void Dispose() {
            _run.Dispose();
            _finish.Dispose();
        }

        static void Cancel(CancellationTokenSource source) {
            try {
                source.Cancel();
            } catch (ObjectDisposedException) {
                // The source has ended.
            }
        }
    }
}
