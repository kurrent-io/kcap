namespace Capacitor.Cli.Daemon.Services;

/// <summary>A compaction the daemon started for an agent, and whether it can still be queued or running. A runtime that
/// cannot say when its compaction ends has it end at the agent's next SessionStart, which is how Claude reports a
/// compaction done, once the command is written.</summary>
internal sealed class RunningCompaction(CompactionStart start, long startedAt, TimeProvider time) {
    readonly Task  _written  = start.Written ?? Task.CompletedTask;
    readonly Task? _finished = start.Finished;
    int _sessionStarted;

    /// <summary>The agent's SessionStart hook ran. Kept even while the write is still settling, since the agent can
    /// report the compaction before the write's own completion is observed.</summary>
    public void NoteSessionStarted() => Volatile.Write(ref _sessionStarted, 1);

    /// <summary>The compaction has ended or failed to start. Past <paramref name="hold"/>, a written compaction whose end
    /// the runtime cannot report is taken as over; one still queued, or whose end the runtime will report, is not.</summary>
    public bool IsOver(TimeSpan hold) =>
        _written.IsFaulted || _written.IsCanceled
     || _finished?.IsCompleted == true
     || _finished is null && _written.IsCompletedSuccessfully
                          && (Volatile.Read(ref _sessionStarted) == 1 || time.GetElapsedTime(startedAt) >= hold);
}
