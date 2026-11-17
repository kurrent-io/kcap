using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RunningCompactionTests {
    static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    static FakeTimeProvider Clock() => new();

    static RunningCompaction Start(FakeTimeProvider time, Task written, Task? finished) =>
        new(CompactionStart.Started(written, finished), time);

    /// <summary>A runtime that reports the end holds until then, however long that takes.</summary>
    [Test]
    public async Task A_runtime_that_reports_the_end_holds_until_then() {
        var time     = Clock();
        var finished = new TaskCompletionSource();
        var running  = Start(time, Task.CompletedTask, finished.Task);

        time.Advance(Hold * 3);
        await Assert.That(running.IsOver(Hold)).IsFalse();

        finished.SetResult();
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    [Test]
    public async Task A_queued_compaction_holds_however_long_it_waits() {
        var time    = Clock();
        var running = Start(time, new TaskCompletionSource().Task, finished: null);

        time.Advance(Hold * 3);

        await Assert.That(running.IsOver(Hold)).IsFalse();
    }

    [Test]
    public async Task A_write_that_failed_holds_nothing() {
        var time    = Clock();
        var failed  = Task.FromException(new IOException("gone"));
        _ = failed.Exception;
        var running = Start(time, failed, finished: null);

        await Assert.That(running.IsOver(Hold)).IsTrue();
    }

    /// <summary>The hold runs from the write, not from the request: a command queued for longer still holds after it.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task A_terminal_compaction_is_over_a_hold_after_its_write(int queuedMinutes) {
        var time    = Clock();
        var written = new TaskCompletionSource();
        var running = Start(time, written.Task, finished: null);

        time.Advance(TimeSpan.FromMinutes(queuedMinutes));
        written.SetResult();
        time.Advance(Hold - TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(running.IsOver(Hold)).IsTrue();
    }
}
