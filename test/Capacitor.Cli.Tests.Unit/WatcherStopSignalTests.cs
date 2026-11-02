using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Windows has no SIGTERM, so a stopper asks a watcher to run its shutdown path through a named event.</summary>
public class WatcherStopSignalTests {
    static int UnusedPid() => Random.Shared.Next(1_000_000, int.MaxValue);

    [Test, RunOn(OS.Windows)]
    public async Task A_request_reaches_the_listening_watcher() {
        var pid   = UnusedPid();
        var asked = new TaskCompletionSource();

        using var listener = WatcherStopSignal.Listen(pid, () => asked.TrySetResult());

        await Assert.That(listener).IsNotNull();
        await Assert.That(WatcherStopSignal.TryRequest(pid)).IsTrue();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>False is what sends the stopper on to the hard kill. The pool's wait thread can keep a disposed
    /// listener's event openable for a moment, so the answer after a dispose is awaited, not read once.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Nobody_listening_reports_the_request_undelivered() {
        var pid = UnusedPid();

        await Assert.That(WatcherStopSignal.TryRequest(pid)).IsFalse();

        WatcherStopSignal.Listen(pid, () => { })!.Dispose();

        var deadline = DateTime.UtcNow + BoundedWait.Deadline;
        while (WatcherStopSignal.TryRequest(pid) && DateTime.UtcNow < deadline) await Task.Delay(20);

        await Assert.That(WatcherStopSignal.TryRequest(pid)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Unix_keeps_sigterm() {
        await Assert.That(WatcherStopSignal.Listen(UnusedPid(), () => { })).IsNull();
        await Assert.That(WatcherStopSignal.TryRequest(UnusedPid())).IsFalse();
    }
}
