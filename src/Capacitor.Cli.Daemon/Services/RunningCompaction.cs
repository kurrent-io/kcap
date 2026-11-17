namespace Capacitor.Cli.Daemon.Services;

/// <summary>A compaction the daemon started for an agent, and whether it can still be queued or running. One whose
/// runtime cannot report its end is taken as running for a fixed hold after its command is written.</summary>
internal sealed class RunningCompaction {
    readonly Task  _written;
    readonly Task? _finished;
    readonly TimeProvider _time;
    long _writtenAt;
    int  _writtenStamped;

    public RunningCompaction(CompactionStart start, TimeProvider time) {
        _written  = start.Written ?? Task.CompletedTask;
        _finished = start.Finished;
        _time     = time;
        _written.ContinueWith(_ => {
                Volatile.Write(ref _writtenAt, time.GetTimestamp());
                Volatile.Write(ref _writtenStamped, 1);
            }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The compaction has ended or failed to start, or, where the runtime cannot report its end, was written
    /// at least <paramref name="hold"/> ago. One still queued, or whose end the runtime will report, is not over.</summary>
    public bool IsOver(TimeSpan hold) =>
        _written.IsFaulted || _written.IsCanceled
     || _finished?.IsCompleted == true
     || _finished is null && Volatile.Read(ref _writtenStamped) == 1 && _time.GetElapsedTime(Volatile.Read(ref _writtenAt)) >= hold;
}
