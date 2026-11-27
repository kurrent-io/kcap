using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

public class SessionEndFenceTests {
    static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    [TempDaemonPaths] public required TempDaemonStore Tmp { get; init; }

    ResumeLedgerStore Store => ResumeLedgerStore.For(Tmp.Store, "d1");

    ResumeLedgerStore Seed(ResumeState state, string? runEpoch = "epoch-1", string? launchEpoch = "epoch-1") {
        Store.Write(ResumeLedgerEntries.For("agent-1", state) with { RunEpoch = runEpoch, LaunchEpoch = launchEpoch, SessionId = "session-1" });
        return Store;
    }

    static bool Hold(string? agentId, string? epoch, ResumeLedgerStore? store, string? session = "session-1") =>
        SessionEndFence.ShouldHold(agentId, epoch, session, _ => store, new FakeTimeProvider(Now));

    [Test]
    public async Task No_entry_posts_and_leaves_no_lock_file() {
        await Assert.That(Hold("agent-1", "epoch-1", null)).IsFalse();
        await Assert.That(Hold(null, "epoch-1", Seed(ResumeState.Resuming))).IsFalse();
        await Assert.That(File.Exists(ResumeEntryLock.PathFor(Store.EntryPath("agent-1")))).IsFalse();
    }

    [Test]
    public async Task A_resuming_entry_holds() {
        await Assert.That(Hold("agent-1", "epoch-1", Seed(ResumeState.Resuming))).IsTrue();
        await Assert.That(Store.TryRead("agent-1")!.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task A_running_entry_under_the_same_epoch_posts_and_is_stamped() {
        var store = Seed(ResumeState.Running);

        await Assert.That(Hold("agent-1", "epoch-1", store)).IsFalse();

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now);
        await Assert.That(entry.EndCommittedEpoch).IsEqualTo("epoch-1");
        await Assert.That(entry.EndCommittedSession).IsEqualTo("session-1");
    }

    /// <summary>The watcher's idle timeout stamps its end as soft, which a later turn of the session may clear.</summary>
    [Test]
    public async Task A_soft_end_is_stamped_soft() {
        var store = Seed(ResumeState.Running);

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now), soft: true)).IsFalse();

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now);
        await Assert.That(entry.EndCommittedSoft).IsTrue();
    }

    /// <summary>A real end after a soft one replaces it, so no turn can clear the end that is now on its way.</summary>
    [Test]
    public async Task A_real_end_replaces_a_soft_one() {
        var store = Seed(ResumeState.Running);
        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now), soft: true);

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now.AddMinutes(5)))).IsFalse();

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now.AddMinutes(5));
        await Assert.That(entry.EndCommittedSoft).IsNull();
    }

    /// <summary>A soft end never weakens a real one already stamped.</summary>
    [Test]
    public async Task A_soft_end_leaves_a_real_one() {
        var store = Seed(ResumeState.Running);
        Hold("agent-1", "epoch-1", store);

        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now.AddMinutes(5)), soft: true);

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now);
        await Assert.That(entry.EndCommittedSoft).IsNull();
    }

    /// <summary>After a Claude /clear the entry runs a new session while the stamp still names the old one: the current
    /// session's end replaces it, or a restart would resume a run whose end is on its way.</summary>
    [Test]
    public async Task The_current_sessions_end_replaces_an_earlier_sessions_stamp() {
        var store = Seed(ResumeState.Running);
        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-0", _ => store, new FakeTimeProvider(Now));

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now.AddMinutes(5)))).IsFalse();

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedSession).IsEqualTo("session-1");
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now.AddMinutes(5));
    }

    /// <summary>A late end of the earlier session never displaces the current session's.</summary>
    [Test]
    public async Task A_late_end_of_an_earlier_session_leaves_the_current_sessions_stamp() {
        var store = Seed(ResumeState.Running);
        Hold("agent-1", "epoch-1", store);

        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-0", _ => store, new FakeTimeProvider(Now.AddMinutes(5)));

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedSession).IsEqualTo("session-1");
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now);
    }

    /// <summary>Two sessions' real ends both let through before discovery: the stamp then names no session, so it fences
    /// whichever one discovery settles on.</summary>
    [Test]
    public async Task Conflicting_ends_before_discovery_fence_either_session() {
        Store.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running) with { RunEpoch = "epoch-1", LaunchEpoch = "epoch-1", SessionId = null });
        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-a", _ => Store, new FakeTimeProvider(Now));

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-b", _ => Store, new FakeTimeProvider(Now.AddMinutes(5)))).IsFalse();

        var entry = Store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now.AddMinutes(5));
        await Assert.That(entry.EndCommittedSession).IsNull();
        await Assert.That(entry.EndCommittedSoft).IsNull();
    }

    /// <summary>A second idle end of the same session records its own time, so a turn between the two clears neither.</summary>
    [Test]
    public async Task A_second_soft_end_records_its_own_time() {
        var store = Seed(ResumeState.Running);
        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now), soft: true);

        SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now.AddHours(2)), soft: true);

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now.AddHours(2));
        await Assert.That(entry.EndCommittedSoft).IsTrue();
    }

    [Test]
    public async Task An_end_from_an_earlier_run_holds() {
        await Assert.That(Hold("agent-1", "epoch-1", Seed(ResumeState.Running, runEpoch: "epoch-2"))).IsTrue();
        await Assert.That(Store.TryRead("agent-1")!.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task An_end_without_an_epoch_holds_only_once_the_run_was_resumed() {
        await Assert.That(Hold("agent-1", null, Seed(ResumeState.Running))).IsFalse();
        await Assert.That(Hold("agent-1", null, Seed(ResumeState.Running, runEpoch: "epoch-2"))).IsTrue();
    }

    const string DashedSession = "beb87a2e-1116-4f08-be82-bd0da91b236d";

    static ExitedSession ClaimedWithoutAgent(string session) =>
        new(SessionId.Parse(session)!, new ClaimOrigin("claude", "https://kcap.example", null, null));

    /// <summary>The claim was written by a kcap that recorded no agent, and the entry by a daemon that
    /// recorded no launch epoch; this daemon has since resumed the run.</summary>
    [Test]
    public async Task A_claim_without_an_agent_is_held_by_a_resumed_run_of_its_session() {
        Store.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running) with { RunEpoch = "epoch-2", SessionId = DashedSession });

        var held = SessionEndFence.ShouldHold(ClaimedWithoutAgent(DashedSession.Replace("-", "")), Tmp.Store, new FakeTimeProvider(Now));

        await Assert.That(held).IsTrue();
        await Assert.That(Store.TryRead("agent-1")!.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task A_claim_without_an_agent_or_a_ledgered_session_posts() {
        Store.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Resuming) with { SessionId = "0df3418a3ad14e60a1276b9605c3e4ec" });

        await Assert.That(SessionEndFence.ShouldHold(ClaimedWithoutAgent(DashedSession), Tmp.Store, new FakeTimeProvider(Now))).IsFalse();
    }

    [Test]
    public async Task A_lock_held_elsewhere_lets_the_end_post() {
        var store = Seed(ResumeState.Resuming);
        using var held = ResumeEntryLock.TryAcquire(store.EntryPath("agent-1"), TimeSpan.FromSeconds(1));

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now), TimeSpan.FromMilliseconds(40)))
            .IsFalse();
    }

    [Test]
    public async Task An_unreadable_ledger_lets_the_end_post() {
        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => throw new IOException("broken"), new FakeTimeProvider(Now)))
            .IsFalse();
    }
}
