using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RunningCompactionTests {
    static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);

    static RunningCompaction Start(FakeTimeProvider time, Task written, Task? finished, Task? begun = null) =>
        new(CompactionStart.Started(written, finished, begun), time);

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

    [Test]
    public async Task A_queued_compaction_holds_past_the_hold() {
        var time    = new FakeTimeProvider();
        var running = Start(time, new TaskCompletionSource().Task, finished: null, begun: new TaskCompletionSource().Task);

        time.Advance(Hold);

        await Assert.That(running.IsOver(Hold)).IsFalse();
    }

    /// <summary>A SessionStart from input still ahead of the command is not the compaction's end.</summary>
    [Test]
    public async Task A_session_start_before_the_command_begins_is_not_its_end() {
        var time    = new FakeTimeProvider();
        var begun   = new TaskCompletionSource();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null, begun.Task);

        running.NoteSessionStarted();
        begun.SetResult();
        written.SetResult();

        await Assert.That(running.IsOver(Hold)).IsFalse();
    }

    /// <summary>A SessionStart while the command is being written still ends the compaction once the write settles.</summary>
    [Test]
    public async Task A_session_start_while_the_command_is_written_ends_it_once_the_write_settles() {
        var time    = new FakeTimeProvider();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null, begun: Task.CompletedTask);

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

    /// <summary>The hold runs from the write, not from the request: a command queued for longer still holds after it.</summary>
    [Test]
    public async Task A_terminal_compaction_whose_end_is_never_heard_is_over_a_hold_after_its_write() {
        var time    = new FakeTimeProvider();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null);

        time.Advance(Hold * 2);
        written.SetResult();
        time.Advance(Hold - TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }
}
