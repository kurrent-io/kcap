using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatcherHeartbeatTests {
    [Test]
    public async Task within_startup_grace_is_never_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: null, startupAt: start, now: start.AddSeconds(5),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsFalse();
    }

    [Test]
    public async Task old_heartbeat_after_grace_is_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: start.AddSeconds(35), startupAt: start,
                                             now: start.AddSeconds(90),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsTrue();
    }

    [Test]
    public async Task recent_heartbeat_after_grace_is_not_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: start.AddSeconds(89), startupAt: start,
                                             now: start.AddSeconds(90),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsFalse();
    }

    [Test]
    public async Task null_heartbeat_after_grace_is_stale() {
        // A watcher that never wrote a heartbeat at all (e.g. crashed before its first
        // main-loop iteration) must read as stale once past the startup grace — otherwise
        // a null lastBeat could be mistaken for "healthy" forever.
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: null, startupAt: start, now: start.AddSeconds(31),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsTrue();
    }

    [Test]
    public async Task touch_then_read_roundtrips() {
        using var tmp = new TempDir();
        var p = WatcherHeartbeat.HeartbeatPath(tmp.Path, "sess");
        var now = DateTimeOffset.UtcNow;
        WatcherHeartbeat.Touch(p, now);
        var read = WatcherHeartbeat.Read(p);
        await Assert.That(read).IsNotNull();
        await Assert.That((read!.Value - now).Duration()).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task read_missing_file_returns_null() {
        using var tmp = new TempDir();
        var p = WatcherHeartbeat.HeartbeatPath(tmp.Path, "missing");
        await Assert.That(WatcherHeartbeat.Read(p)).IsNull();
    }

    [Test]
    public async Task touch_overwrites_previous_value() {
        using var tmp = new TempDir();
        var p = WatcherHeartbeat.HeartbeatPath(tmp.Path, "sess");
        WatcherHeartbeat.Touch(p, DateTimeOffset.UtcNow.AddMinutes(-5));
        var second = DateTimeOffset.UtcNow;
        WatcherHeartbeat.Touch(p, second);
        var read = WatcherHeartbeat.Read(p);
        await Assert.That(read).IsNotNull();
        await Assert.That((read!.Value - second).Duration()).IsLessThan(TimeSpan.FromSeconds(1));
    }
}
