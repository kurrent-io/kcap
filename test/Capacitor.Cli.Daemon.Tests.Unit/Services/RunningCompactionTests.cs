using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RunningCompactionTests {
    static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);

    static RunningCompaction Start(FakeTimeProvider time, Task written, Task? finished) =>
        new(CompactionStart.Started(written, finished), time.GetTimestamp(), time);

    [Test]
    public async Task A_runtime_that_reports_the_end_holds_until_then() {
        var time     = new FakeTimeProvider();
        var finished = new TaskCompletionSource();
        var running  = Start(time, Task.CompletedTask, finished.Task);

        running.NoteSessionStarted();
        await Assert.That(running.IsOver(Hold)).IsFalse();

        finished.SetResult();
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    /// <summary>A SessionStart before the command was written is the agent's own start, not the compaction's end.</summary>
    [Test]
    public async Task A_terminal_compaction_ends_at_the_session_start_after_its_write() {
        var time    = new FakeTimeProvider();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null);

        running.NoteSessionStarted();
        await Assert.That(running.IsOver(Hold)).IsFalse();

        written.SetResult();
        running.NoteSessionStarted();
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
    public async Task An_end_never_heard_is_taken_past_the_hold() {
        var time    = new FakeTimeProvider();
        var running = Start(time, Task.CompletedTask, finished: null);

        time.Advance(Hold - TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }
}
