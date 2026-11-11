using Capacitor.Cli.Daemon.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// The timing both terminal readers share, for a child whose terminal a descendant keeps writing to.
public class PtyExitWatchTests {
    /// While output keeps coming, the exit is asked about once per gap rather than on every read or never.
    [Test]
    public async Task Output_that_never_stops_still_has_the_exit_checked_once_per_gap() {
        var time   = new FakeTimeProvider();
        var asks   = 0;
        var exited = false;
        var watch  = new PtyExitWatch(() => {
                asks++;

                return exited;
            }, time);

        watch.CheckIfDue();
        time.Advance(PtyExitWatch.CheckGap - TimeSpan.FromMilliseconds(1));
        watch.CheckIfDue();
        await Assert.That(asks).IsEqualTo(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        watch.CheckIfDue();
        watch.CheckIfDue();
        await Assert.That(asks).IsEqualTo(1);

        exited = true;
        time.Advance(PtyExitWatch.CheckGap);
        watch.CheckIfDue();
        await Assert.That(watch.ChildGone).IsTrue();
    }

    /// Once the exit is seen the reader reads on for a bounded time, however much is still being written.
    [Test]
    public async Task The_drain_after_the_exit_ends_at_its_limit() {
        var time  = new FakeTimeProvider();
        var watch = new PtyExitWatch(() => true, time);

        await Assert.That(watch.DrainOver).IsFalse();

        watch.Check();
        time.Advance(PtyExitWatch.DrainLimit - TimeSpan.FromMilliseconds(1));
        await Assert.That(watch.DrainOver).IsFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(watch.DrainOver).IsTrue();
    }

    /// Time spent handing a chunk to a slow consumer does not count against the drain, only time spent reading.
    [Test]
    public async Task Waiting_on_the_consumer_does_not_use_up_the_drain() {
        var time  = new FakeTimeProvider();
        var watch = new PtyExitWatch(() => true, time);

        watch.Check();
        time.Advance(PtyExitWatch.DrainLimit / 2);
        watch.Handing();
        time.Advance(PtyExitWatch.DrainLimit * 3);
        watch.Reading();
        await Assert.That(watch.DrainOver).IsFalse();

        time.Advance(PtyExitWatch.DrainLimit / 2);
        await Assert.That(watch.DrainOver).IsTrue();
    }
}
