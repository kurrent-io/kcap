namespace Capacitor.Cli.Daemon.Services;

/// <summary>A compaction the daemon started for an agent, and whether it can still be running. A runtime that cannot
/// say when its compaction ends has it end at the agent's next SessionStart after the command was written, which is
/// how Claude reports a compaction done.</summary>
internal sealed class RunningCompaction(CompactionStart start, long startedAt, TimeProvider time) {
    readonly Task  _written  = start.Written ?? Task.CompletedTask;
    readonly Task? _finished = start.Finished;
    int _sessionStarted;

    /// <summary>The agent's SessionStart hook ran.</summary>
    public void NoteSessionStarted() {
        if (_written.IsCompletedSuccessfully) Volatile.Write(ref _sessionStarted, 1);
    }

    /// <summary>The compaction has ended, failed to start, or has been running for longer than <paramref name="hold"/>,
    /// past which a compaction whose end was never heard is taken as over.</summary>
    public bool IsOver(TimeSpan hold) =>
        _written.IsFaulted || _written.IsCanceled
     || _finished?.IsCompleted == true
     || _finished is null && Volatile.Read(ref _sessionStarted) == 1
     || time.GetElapsedTime(startedAt) >= hold;
}
