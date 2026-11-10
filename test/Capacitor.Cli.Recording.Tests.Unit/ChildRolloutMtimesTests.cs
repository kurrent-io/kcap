namespace Capacitor.Cli.Recording.Tests.Unit;

public class ChildRolloutMtimesTests {
    // A ceiling-reaped child whose subagent re-engages would grow its rollout with nobody watching, so the
    // scan re-ensures the child watcher whenever the rollout's mtime advances. This pure gate decides when,
    // recording the mtime it fired for so an unchanged rollout stays quiet.
    [Test]
    public async Task Advanced_fires_on_first_sight_and_then_only_on_mtime_change() {
        var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var t0     = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0)).IsTrue();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0)).IsFalse();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0.AddSeconds(1))).IsTrue();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0.AddSeconds(1))).IsFalse();
    }

    // Distinct rollouts are tracked independently — one child's growth must not re-ensure another.
    [Test]
    public async Task Advanced_tracks_each_rollout_independently() {
        var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var t0     = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0)).IsTrue();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/b.jsonl", t0)).IsTrue();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/a.jsonl", t0.AddSeconds(5))).IsTrue();
        await Assert.That(ChildRolloutMtimes.Advanced(mtimes, "/b.jsonl", t0)).IsFalse();
    }
}
