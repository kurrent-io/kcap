using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class HeartbeatSlicesTests {
    // --- Connect-retry / reconnect heartbeat freshness ---
    // The connect-retry backoff grows to 30s, longer than the 20s staleness threshold. The wait
    // is chunked via HeartbeatSlices so the heartbeat is refreshed between slices; if any single
    // chunk could exceed the threshold, a healthy-but-reconnecting watcher would be falsely reaped.

    [Test]
    public async Task heartbeat_slices_never_exceed_the_max_slice() {
        // The worst case: a full 30s backoff wait. Every chunk must stay under the staleness
        // threshold so a reconnecting watcher can never look wedged.
        var slices = TranscriptRecorder.HeartbeatSlices(TimeSpan.FromSeconds(30), TranscriptRecorder.HeartbeatSlice);

        await Assert.That(slices.Count).IsGreaterThan(1);
        foreach (var s in slices) {
            await Assert.That(s).IsLessThanOrEqualTo(TranscriptRecorder.HeartbeatSlice);
            await Assert.That(s).IsLessThan(WatcherHeartbeat.Threshold);
        }
    }

    [Test]
    public async Task heartbeat_slices_sum_to_the_total_wait() {
        var total  = TimeSpan.FromSeconds(23);
        var slices = TranscriptRecorder.HeartbeatSlices(total, TranscriptRecorder.HeartbeatSlice);

        var sum = TimeSpan.Zero;
        foreach (var s in slices) sum += s;

        await Assert.That(sum).IsEqualTo(total);
    }

    [Test]
    public async Task heartbeat_slices_short_wait_is_a_single_chunk() {
        var slices = TranscriptRecorder.HeartbeatSlices(TimeSpan.FromSeconds(3), TranscriptRecorder.HeartbeatSlice);

        await Assert.That(slices.Count).IsEqualTo(1);
        await Assert.That(slices[0]).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task heartbeat_slices_non_positive_total_yields_no_chunks() {
        await Assert.That(TranscriptRecorder.HeartbeatSlices(TimeSpan.Zero, TranscriptRecorder.HeartbeatSlice).Count).IsEqualTo(0);
        await Assert.That(TranscriptRecorder.HeartbeatSlices(TimeSpan.FromSeconds(-5), TranscriptRecorder.HeartbeatSlice).Count).IsEqualTo(0);
    }
}
