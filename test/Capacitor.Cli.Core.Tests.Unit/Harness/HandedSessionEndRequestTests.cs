using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

public class HandedSessionEndRequestTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "01a13694b0ad7ae28bf52af6357a0847";

    [Test]
    public async Task A_written_request_is_claimed_once_and_gone_once_released() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, """{"session_id":"x","reason":"exit"}""");
        await Assert.That(HandedSessionEndRequest.Exists(paths, SessionId)).IsTrue();

        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher");
        await Assert.That(claimed).IsNotNull();
        await Assert.That(claimed!.Payload).IsEqualTo("""{"session_id":"x","reason":"exit"}""");
        await Assert.That(HandedSessionEndRequest.Exists(paths, SessionId)).IsFalse();

        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "hook")).IsNull();

        HandedSessionEndRequest.Release(claimed.ClaimedPath);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
    }

    /// <summary>The vendor whose hook handed the end over names its route, and survives a deferral.</summary>
    [Test]
    public async Task A_request_keeps_the_vendor_that_handed_it_over() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", "https://kcap.example", vendor: "codex");
        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher", time)!;

        await Assert.That(claimed.Vendor).IsEqualTo("codex");
        await Assert.That(claimed.Route).IsEqualTo("session-end/codex");
        await Assert.That(claimed.Payload).IsEqualTo("""{"reason":"other"}""");

        HandedSessionEndRequest.Defer(claimed, time);
        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "retry", time)!.Vendor).IsEqualTo("codex");
    }

    /// <summary>A kcap that predates other vendors reads every <c>.muse-end-requested</c> file as Muse's, so a Codex
    /// request is named for Codex, and this kcap still lists, takes and deletes it.</summary>
    [Test]
    public async Task A_codex_request_is_named_for_codex_where_older_kcaps_never_take_it() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex");

        await Assert.That(Directory.GetFiles(paths.Directory).Select(f => Path.GetFileName(f)!).ToList()).IsEquivalentTo([$"{SessionId}.codex-end-requested"]);
        await Assert.That(HandedSessionEndRequest.SessionsWithPending(paths)).IsEquivalentTo([SessionId]);
        await Assert.That(HandedSessionEndRequest.Exists(paths, SessionId)).IsTrue();

        HandedSessionEndRequest.DeleteAll(paths, SessionId);
        await Assert.That(Directory.GetFiles(paths.Directory)).IsEmpty();
    }

    /// <summary>A watcher that died holding its claim never sends the end nor returns it: the next sweep puts it back.
    /// A claim whose holder still runs, or that names no process, is left alone.</summary>
    [Test]
    public async Task A_claim_whose_watcher_died_goes_back_for_the_next_taker() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        await exited.WaitForExitAsync();

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex");
        HandedSessionEndRequest.TryClaim(paths, SessionId, exited.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        HandedSessionEndRequest.Write(paths, "live", """{"reason":"other"}""");
        HandedSessionEndRequest.TryClaim(paths, "live", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        HandedSessionEndRequest.Write(paths, "swept", """{"reason":"other"}""");
        HandedSessionEndRequest.TryClaim(paths, "swept", "sweep");

        HandedSessionEndRequest.RecoverAbandoned(paths);

        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "next")!.Vendor).IsEqualTo("codex");
        await Assert.That(HandedSessionEndRequest.Exists(paths, "live")).IsFalse();
        await Assert.That(HandedSessionEndRequest.Exists(paths, "swept")).IsFalse();
    }

    /// <summary>A file that ends like a request but names no session is no request, and listing goes on past it.</summary>
    [Test]
    public async Task A_request_name_without_a_session_is_skipped() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex");
        File.WriteAllText(Path.Combine(paths.Directory, "codex-end-requested"), "");

        await Assert.That(HandedSessionEndRequest.SessionsWithPending(paths)).IsEquivalentTo([SessionId]);
    }

    /// <summary>A resume on one server cancels only the ends owed to it: one still owed to another server is posted there.</summary>
    [Test]
    public async Task A_resume_discards_only_the_ends_owed_to_its_server() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other","agent_host_id":"a","daemon_epoch":"1"}""", "https://a.example", vendor: "codex");
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other","agent_host_id":"b","daemon_epoch":"1"}""", "https://b.example", vendor: "codex");
        foreach (var file in Directory.GetFiles(paths.Directory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));

        HandedSessionEndRequest.DiscardForResume(paths, SessionId, "https://b.example");

        var left = HandedSessionEndRequest.TryClaim(paths, SessionId, "check");
        await Assert.That(left!.Server).IsEqualTo("https://a.example");
        await Assert.That(HandedSessionEndRequest.ResumedSince(paths, SessionId, left.HandedAt, left.Server)).IsFalse();
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "check")).IsNull();
    }

    /// <summary>The sweep and a resume claim as their process too, so a claim either dies holding goes back.</summary>
    [Test]
    public async Task Every_claim_names_the_claiming_process() =>
        await Assert.That(HandedSessionEndRequest.OwnClaim).IsEqualTo(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A session id is matched literally, so one that reads as a search pattern reaches no other session's end;
    /// and in any case, since a hook and a watcher may spell it differently.</summary>
    [Test]
    public async Task A_session_is_matched_literally_and_in_any_case() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex");

        await Assert.That(HandedSessionEndRequest.TryClaim(paths, "01a1*", "glob")).IsNull();
        HandedSessionEndRequest.DeleteAll(paths, "*");
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId.ToUpperInvariant(), "upper")).IsNotNull();
    }

    /// <summary>The process that handed the end over travels with it through a deferral.</summary>
    [Test]
    public async Task A_request_keeps_the_process_that_handed_it_over() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex", agentPid: 4242);

        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher", time)!;
        await Assert.That(claimed.AgentPid).IsEqualTo(4242);

        HandedSessionEndRequest.Defer(claimed, time);
        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "retry", time)!.AgentPid).IsEqualTo(4242);
    }

    /// <summary>An end is outstanding while it waits and while a taker holds it, so the exit reaper leaves it alone.</summary>
    [Test]
    public async Task A_request_is_outstanding_until_released() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""", vendor: "codex");
        await Assert.That(HandedSessionEndRequest.Outstanding(paths, SessionId)).IsTrue();

        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher")!;
        await Assert.That(HandedSessionEndRequest.Outstanding(paths, SessionId)).IsTrue();

        HandedSessionEndRequest.Release(claimed.ClaimedPath);
        await Assert.That(HandedSessionEndRequest.Outstanding(paths, SessionId)).IsFalse();
    }

    /// <summary>A request written before requests recorded a vendor is Muse's, the only vendor that handed ends over then.</summary>
    [Test]
    public async Task A_request_with_no_vendor_is_muses() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"other"}""");
        File.WriteAllText(HandedSessionEndRequest.PathFor(paths, SessionId), """{"reason":"other"}""");

        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher")!;

        await Assert.That(claimed.Vendor).IsEqualTo("muse");
        await Assert.That(claimed.Route).IsEqualTo("session-end/muse");
    }

    [Test]
    [Arguments("muse", true)]
    [Arguments("codex", true)]
    [Arguments("claude", false)]
    [Arguments("cursor", false)]
    public async Task Only_muse_and_codex_hand_their_end_over(string vendor, bool expected) =>
        await Assert.That(HandedSessionEndRequest.HandedOverBy(vendor)).IsEqualTo(expected);

    [Test]
    public async Task An_unclaimed_request_is_there_for_the_next_taker_unless_a_newer_one_arrived() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"first"}""");
        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "w1")!;

        HandedSessionEndRequest.Unclaim(claimed);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w2")!.Payload).IsEqualTo("""{"reason":"first"}""");

        var stale = HandedSessionEndRequest.TryClaim(paths, SessionId, "w3");
        await Assert.That(stale).IsNull();

        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"old"}""");
        var old = HandedSessionEndRequest.TryClaim(paths, SessionId, "w4")!;
        HandedSessionEndRequest.Write(paths, SessionId, """{"reason":"newer"}""");

        HandedSessionEndRequest.Unclaim(old);
        await Assert.That(File.Exists(old.ClaimedPath)).IsFalse();
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w5")!.Payload).IsEqualTo("""{"reason":"newer"}""");
    }

    const string E2 = """{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-2"}""";
    const string E1 = """{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-1"}""";

    /// <summary>Each run's end has its own request: an earlier run's late hook writing after the live run's neither
    /// overwrites it nor supersedes its claim, and the two are taken oldest first.</summary>
    [Test]
    public async Task An_earlier_runs_late_request_neither_overwrites_nor_supersedes_the_live_runs() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, E2);
        File.SetLastWriteTimeUtc(HandedSessionEndRequest.PathFor(paths, SessionId, E2), DateTime.UtcNow.AddSeconds(-2));
        var live = HandedSessionEndRequest.TryClaim(paths, SessionId, "w1")!;
        HandedSessionEndRequest.Write(paths, SessionId, E1);

        await Assert.That(live.Payload).IsEqualTo(E2);
        HandedSessionEndRequest.Unclaim(live);

        await Assert.That(File.Exists(live.ClaimedPath)).IsFalse();
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w2")!.Payload).IsEqualTo(E2);
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w3")!.Payload).IsEqualTo(E1);
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w4")).IsNull();
    }

    /// <summary>A request whose end could be neither sent nor kept is not taken again until its backoff has passed, and
    /// the backoff doubles with each failure: whatever failed would likely fail the same way at once.</summary>
    [Test]
    public async Task A_deferred_request_waits_out_a_doubling_backoff() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        HandedSessionEndRequest.Write(paths, SessionId, E2);

        HandedSessionEndRequest.Defer(HandedSessionEndRequest.TryClaim(paths, SessionId, "w1", time)!, time);
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w2", time)).IsNull();
        time.Advance(TimeSpan.FromSeconds(31));
        var again = HandedSessionEndRequest.TryClaim(paths, SessionId, "w3", time)!;
        await Assert.That(again.Payload).IsEqualTo(E2);

        HandedSessionEndRequest.Defer(again, time);
        time.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w4", time)).IsNull();
        time.Advance(TimeSpan.FromSeconds(30));
        var third = HandedSessionEndRequest.TryClaim(paths, SessionId, "w5", time)!;

        HandedSessionEndRequest.Release(third.ClaimedPath);
        HandedSessionEndRequest.Write(paths, SessionId, E2);
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "w6", time)).IsNotNull();
    }

    /// <summary>A request is published by one rename with the server it is for and its backoff inside it, so nothing a
    /// claimant of an earlier request removes can strip a newer request of its server.</summary>
    [Test]
    public async Task A_request_carries_its_server_and_backoff_in_the_one_file_it_is_published_as() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        HandedSessionEndRequest.Write(paths, SessionId, E2, "https://kcap.example");

        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "w1", time)!;
        await Assert.That(claimed.Server).IsEqualTo("https://kcap.example");
        await Assert.That(claimed.Payload).IsEqualTo(E2);

        HandedSessionEndRequest.Defer(claimed, time);
        await Assert.That(Directory.GetFiles(paths.Directory)).IsEquivalentTo([HandedSessionEndRequest.PathFor(paths, SessionId, E2)]);

        time.Advance(TimeSpan.FromMinutes(1));
        var again = HandedSessionEndRequest.TryClaim(paths, SessionId, "w2", time)!;
        await Assert.That((again.Server, again.Attempts, again.Payload)).IsEqualTo(((string?)"https://kcap.example", 1, E2));
    }

    [Test]
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();
        await Assert.That(HandedSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }

    /// <summary>The hand-over time, not the claim time, is what a resume is measured against: a claim
    /// taken late still carries an end older than the session's new life.</summary>
    [Test]
    public async Task A_resume_after_the_hand_over_makes_the_end_stale_whenever_it_is_claimed() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        HandedSessionEndRequest.Write(paths, SessionId, "{}");
        var handedAt = File.GetLastWriteTimeUtc(HandedSessionEndRequest.PathFor(paths, SessionId));
        File.SetLastWriteTimeUtc(HandedSessionEndRequest.PathFor(paths, SessionId), handedAt.AddMinutes(-2));

        HandedSessionEndRequest.MarkResumed(paths, SessionId);
        var claimed = HandedSessionEndRequest.TryClaim(paths, SessionId, "watcher")!;

        await Assert.That(claimed.HandedAt.UtcDateTime).IsEqualTo(handedAt.AddMinutes(-2));
        await Assert.That(HandedSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt)).IsTrue();
        await Assert.That(HandedSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt.AddMinutes(5))).IsFalse();
        await Assert.That(HandedSessionEndRequest.ResumedSince(paths, "0000000000000000000000000000beef", claimed.HandedAt)).IsFalse();
    }
}
