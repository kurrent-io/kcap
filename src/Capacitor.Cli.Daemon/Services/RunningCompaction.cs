namespace Capacitor.Cli.Daemon.Services;

/// <summary>A compaction the daemon started for an agent, and whether it can still be queued or running. A runtime that
/// cannot say when its compaction ends has it end at the agent's first SessionStart once the command has begun to be
/// sent, which is how Claude reports a compaction done.</summary>
internal sealed class RunningCompaction {
    readonly Task         _written;
    readonly Task?        _finished;
    readonly Task         _begun;
    readonly TimeProvider _time;
    long _writtenAt;
    int  _sessionStarted;

    public RunningCompaction(CompactionStart start, TimeProvider time) {
        _written  = start.Written ?? Task.CompletedTask;
        _finished = start.Finished;
        _begun    = start.Begun ?? Task.CompletedTask;
        _time     = time;
        _written.ContinueWith(_ => Volatile.Write(ref _writtenAt, time.GetTimestamp()), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The agent's SessionStart hook ran. One from input still ahead of the command in the queue is not its
    /// end; one while the command is being written is kept, since the agent can report it before the write settles.</summary>
    public void NoteSessionStarted() {
        if (_begun.IsCompleted) Volatile.Write(ref _sessionStarted, 1);
    }

    /// <summary>The compaction has ended or failed to start. A written compaction whose end the runtime cannot report is
    /// taken as over <paramref name="hold"/> after its write; one still queued, or whose end the runtime will report, is
    /// not.</summary>
    public bool IsOver(TimeSpan hold) =>
        _written.IsFaulted || _written.IsCanceled
     || _finished?.IsCompleted == true
     || _finished is null && _written.IsCompletedSuccessfully && Volatile.Read(ref _writtenAt) is var writtenAt && writtenAt != 0
                          && (Volatile.Read(ref _sessionStarted) == 1 || _time.GetElapsedTime(writtenAt) >= hold);
}
