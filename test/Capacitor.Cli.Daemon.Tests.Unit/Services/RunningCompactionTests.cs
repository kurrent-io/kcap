using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RunningCompactionTests {
    static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);

    static RunningCompaction Start(FakeTimeProvider time, Task written, Task? finished) =>
        new(CompactionStart.Started(written, finished), time.GetTimestamp(), time);

    /// <summary>A runtime that reports the end holds until then, past the hold and past a SessionStart.</summary>
    [Test]
    public async Task A_runtime_that_reports_the_end_holds_until_then() {
        var time     = new FakeTimeProvider();
        var finished = new TaskCompletionSource();
        var running  = Start(time, Task.CompletedTask, finished.Task);

        running.NoteSessionStarted();
        time.Advance(Hold);
        await Assert.That(running.IsOver(Hold)).IsFalse();

        finished.SetResult();
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    /// <summary>A queued compaction holds past the hold: the hold covers only a written one whose end is never heard.</summary>
    [Test]
    public async Task A_queued_compaction_holds_past_the_hold() {
        var time     = new FakeTimeProvider();
        var written  = new TaskCompletionSource();
        var running  = Start(time, written.Task, new TaskCompletionSource().Task);

        time.Advance(Hold);

        await Assert.That(running.IsOver(Hold)).IsFalse();
    }

    /// <summary>A SessionStart that lands while the write is still settling still ends the compaction once it settles.</summary>
    [Test]
    public async Task A_terminal_compaction_ends_at_a_session_start_seen_before_its_write_settled() {
        var time    = new FakeTimeProvider();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null);

        running.NoteSessionStarted();
        await Assert.That(running.IsOver(Hold)).IsFalse();

        written.SetResult();
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    [Test]
    public async Task A_write_that_failed_holds_nothing() {
        var time    = new FakeTimeProvider();
        var failed  = Task.FromException(new IOException("gone"));
        _ = failed.Exception;
        var running = Start(time, failed, finished: null);

        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    [Test]
    public async Task A_written_terminal_compaction_whose_end_is_never_heard_is_over_past_the_hold() {
        var time    = new FakeTimeProvider();
        var running = Start(time, Task.CompletedTask, finished: null);

        time.Advance(Hold - TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }
}
