using Capacitor.Cli.Core.Harness.Muse;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseSessionEndRequestTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "01a13694b0ad7ae28bf52af6357a0847";

    [Test]
    public async Task A_written_request_is_claimed_once_and_gone_once_released() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, """{"session_id":"x","reason":"exit"}""");
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsTrue();

        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher");
        await Assert.That(claimed).IsNotNull();
        await Assert.That(claimed!.Payload).IsEqualTo("""{"session_id":"x","reason":"exit"}""");
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();

        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "hook")).IsNull();

        MuseSessionEndRequest.Release(claimed.ClaimedPath);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
    }

    [Test]
    public async Task An_unclaimed_request_is_there_for_the_next_taker_unless_a_newer_one_arrived() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"first"}""");
        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "w1")!;

        MuseSessionEndRequest.Unclaim(claimed);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w2")!.Payload).IsEqualTo("""{"reason":"first"}""");

        var stale = MuseSessionEndRequest.TryClaim(paths, SessionId, "w3");
        await Assert.That(stale).IsNull();

        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"old"}""");
        var old = MuseSessionEndRequest.TryClaim(paths, SessionId, "w4")!;
        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"newer"}""");

        MuseSessionEndRequest.Unclaim(old);
        await Assert.That(File.Exists(old.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w5")!.Payload).IsEqualTo("""{"reason":"newer"}""");
    }

    const string E2 = """{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-2"}""";
    const string E1 = """{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-1"}""";

    /// <summary>Each run's end has its own request: an earlier run's late hook writing after the live run's neither
    /// overwrites it nor supersedes its claim, and the two are taken oldest first.</summary>
    [Test]
    public async Task An_earlier_runs_late_request_neither_overwrites_nor_supersedes_the_live_runs() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, E2);
        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, SessionId, E2), DateTime.UtcNow.AddSeconds(-2));
        var live = MuseSessionEndRequest.TryClaim(paths, SessionId, "w1")!;
        MuseSessionEndRequest.Write(paths, SessionId, E1);

        await Assert.That(live.Payload).IsEqualTo(E2);
        MuseSessionEndRequest.Unclaim(live);

        await Assert.That(File.Exists(live.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w2")!.Payload).IsEqualTo(E2);
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w3")!.Payload).IsEqualTo(E1);
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w4")).IsNull();
    }

    /// <summary>A request whose end could be neither sent nor kept is not taken again until its backoff has passed, and
    /// the backoff doubles with each failure: whatever failed would likely fail the same way at once.</summary>
    [Test]
    public async Task A_deferred_request_waits_out_a_doubling_backoff() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        MuseSessionEndRequest.Write(paths, SessionId, E2);

        MuseSessionEndRequest.Defer(MuseSessionEndRequest.TryClaim(paths, SessionId, "w1", time)!, time);
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w2", time)).IsNull();
        time.Advance(TimeSpan.FromSeconds(31));
        var again = MuseSessionEndRequest.TryClaim(paths, SessionId, "w3", time)!;
        await Assert.That(again.Payload).IsEqualTo(E2);

        MuseSessionEndRequest.Defer(again, time);
        time.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w4", time)).IsNull();
        time.Advance(TimeSpan.FromSeconds(30));
        var third = MuseSessionEndRequest.TryClaim(paths, SessionId, "w5", time)!;

        MuseSessionEndRequest.Release(third.ClaimedPath);
        MuseSessionEndRequest.Write(paths, SessionId, E2);
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w6", time)).IsNotNull();
    }

    [Test]
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }

    /// <summary>The hand-over time, not the claim time, is what a resume is measured against: a claim
    /// taken late still carries an end older than the session's new life.</summary>
    [Test]
    public async Task A_resume_after_the_hand_over_makes_the_end_stale_whenever_it_is_claimed() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, "{}");
        var handedAt = File.GetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, SessionId));
        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, SessionId), handedAt.AddMinutes(-2));

        MuseSessionEndRequest.MarkResumed(paths, SessionId);
        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")!;

        await Assert.That(claimed.HandedAt.UtcDateTime).IsEqualTo(handedAt.AddMinutes(-2));
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt)).IsTrue();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt.AddMinutes(5))).IsFalse();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, "0000000000000000000000000000beef", claimed.HandedAt)).IsFalse();
    }
}
