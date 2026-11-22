using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

public class HeapReclaimerTests {
    [Test]
    public async Task A_large_drop_collects_once_the_settle_window_passes() {
        var time = new FakeTimeProvider();
        var collections = 0;
        using var reclaimer = new HeapReclaimer(time, () => collections++);

        reclaimer.Released(HeapReclaimer.LineThreshold);
        time.Advance(HeapReclaimer.Settle - TimeSpan.FromMilliseconds(1));
        await Assert.That(collections).IsEqualTo(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(collections).IsEqualTo(1);

        time.Advance(HeapReclaimer.Settle * 10);
        await Assert.That(collections).IsEqualTo(1);
    }

    [Test]
    public async Task A_small_drop_never_collects() {
        var time = new FakeTimeProvider();
        var collections = 0;
        using var reclaimer = new HeapReclaimer(time, () => collections++);

        reclaimer.Released(HeapReclaimer.LineThreshold - 1);
        time.Advance(HeapReclaimer.Settle * 10);

        await Assert.That(collections).IsEqualTo(0);
    }

    /// <summary>Closing tabs one after another restarts the window, so the run costs one collection, after the last.</summary>
    [Test]
    public async Task Successive_large_drops_collect_once_after_the_last() {
        var time = new FakeTimeProvider();
        var collections = 0;
        using var reclaimer = new HeapReclaimer(time, () => collections++);

        reclaimer.Released(HeapReclaimer.LineThreshold);
        time.Advance(HeapReclaimer.Settle / 2);
        reclaimer.Released(HeapReclaimer.LineThreshold);
        time.Advance(HeapReclaimer.Settle / 2);
        reclaimer.Released(HeapReclaimer.LineThreshold * 3);
        time.Advance(HeapReclaimer.Settle - TimeSpan.FromMilliseconds(1));
        await Assert.That(collections).IsEqualTo(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(collections).IsEqualTo(1);
    }
}
