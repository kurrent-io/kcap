using System.Reactive.Linq;
using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionAccessServiceTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAccessService Service;
        public Harness() => Service = new SessionAccessService(Lane, Time);
        public void Connect(int epoch = 1) => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "closed"));
        public static async Task<SessionAccessState> Current(SessionAccessLease lease) {
            SessionAccessState? state = null;
            using (lease.State.Subscribe(s => state = s)) { }
            return state!.Value;
        }
        public void Dispose() => Service.Dispose();
    }

    [Test]
    public async Task Establishes_in_order_watch_then_chat_and_reports_established() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "chat subscribe");
        await Assert.That(h.Lane.Calls.IndexOf("watch:s1")).IsLessThan(h.Lane.Calls.IndexOf("chat:s1"));
        await Assert.That(h.Lane.AccessWatches).Contains("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        lease.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }

    /// States reach the transition stream in the order they were published, across entries too: an entry replaced from
    /// inside its own observer still delivers its refusal before its successor's establishment, or a reader ordering by
    /// session would let the stale refusal win.
    [Test]
    public async Task A_replaced_entrys_last_state_reaches_transitions_before_its_successors() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        var seen = new List<SessionAccessState>();
        using var transitions = h.Service.Transitions.Where(t => t.SessionId == "s1").Subscribe(t => { lock (seen) seen.Add(t.State); });
        var old = h.Service.Acquire("s1");
        SessionAccessLease? successor = null;
        using var replace = old.State.Where(s => s == SessionAccessState.Denied).Take(1).Subscribe(_ => {
            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            old.Dispose();
            successor = h.Service.Acquire("s1");
        });

        await WaitUntilAsync(() => { lock (seen) return seen.Contains(SessionAccessState.Established); }, what: "the successor established");

        List<SessionAccessState> order;
        lock (seen) order = [.. seen];
        await Assert.That(order.IndexOf(SessionAccessState.Denied)).IsLessThan(order.IndexOf(SessionAccessState.Established));
        successor?.Dispose();
    }

    /// An observer that throws keeps the state from neither the transition stream nor later states.
    [Test]
    public async Task A_throwing_observer_does_not_hide_the_state_from_transitions() {
        using var h = new Harness();
        var seen = new List<SessionAccessState>();
        using var transitions = h.Service.Transitions.Where(t => t.SessionId == "s1").Subscribe(t => { lock (seen) seen.Add(t.State); });
        // Subscribed while the lane is down, so the throwing observer is in place before anything is established.
        using var lease = h.Service.Acquire("s1");
        using var thrower = lease.State.Subscribe(s => { if (s == SessionAccessState.Established) throw new InvalidOperationException("observer"); });
        h.Connect();

        await WaitUntilAsync(() => { lock (seen) return seen.Contains(SessionAccessState.Established); }, what: "established on the transition stream");
    }

    [Test]
    public async Task A_denied_watch_is_terminal_and_never_subscribes_chat() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied");
        await Assert.That(h.Lane.ChatSubscribes).DoesNotContain("s1");
    }

    /// A live workspace can open the session before the server has it, which the server answers
    /// exactly as it answers a hidden one. The recorded session's workspace takes the same id over
    /// after Stop and must be asked again, not handed that verdict.
    [Test]
    public async Task A_new_lease_re_asks_a_session_an_earlier_lease_was_denied() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        using var live = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(live) == SessionAccessState.Denied, "denied before the server had the session");

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        using var recorded = h.Service.Acquire("s1");

        await WaitUntilAsync(async () => await Harness.Current(recorded) == SessionAccessState.Established, "the newcomer re-asked");
        await Assert.That(await Harness.Current(live)).IsEqualTo(SessionAccessState.Established);
    }

    /// Re-asking is not forgiving: a session the server still refuses stays refused, chat and all.
    [Test]
    public async Task A_new_lease_on_a_session_the_server_still_refuses_stays_denied() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        using var first = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(first) == SessionAccessState.Denied, "denied");

        using var second = h.Service.Acquire("s1");

        await WaitUntilAsync(() => h.Lane.AccessWatches.Count(s => s == "s1") == 2, what: "asked again");
        await WaitUntilAsync(async () => await Harness.Current(second) == SessionAccessState.Denied, "still denied");
        await Assert.That(h.Lane.ChatSubscribes).DoesNotContain("s1");
    }

    [Test]
    public async Task Recheck_re_asks_only_a_denied_session() {
        using var h = new Harness();
        h.Connect();
        using var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");

        h.Service.RecheckDenied("s1");
        await Task.Delay(50);
        await Assert.That(h.Lane.AccessWatches.Count(s => s == "s1")).IsEqualTo(1);

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "revoked");
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);

        h.Service.RecheckDenied("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "re-asked once refused");
    }

    /// The re-ask must not lift a refusal it has not overturned: a failed attempt or a lane drop
    /// would read as Offline, which shows the withheld transcript again.
    [Test]
    public async Task A_refusal_stands_through_a_failed_re_ask_and_a_lane_drop_until_access_is_established() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        using var first = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(first) == SessionAccessState.Denied, "first denied");
        var seen = new List<SessionAccessState>();
        using var watch = first.State.Subscribe(s => { lock (seen) seen.Add(s); });

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Failed("transient"));
        using var second = h.Service.Acquire("s1");
        // The ladder's retry is armed in the same section that publishes the failed verdict, so a
        // third ask proves the second was answered.
        await WaitUntilAsync(() => {
            h.Time.Advance(Slice);
            return h.Lane.AccessWatches.Count(s => s == "s1") >= 3;
        }, what: "the failed re-ask retried");
        h.Drop();
        List<SessionAccessState> states;
        lock (seen) states = [.. seen];
        // Asking again may show Establishing, which shows nothing; Unavailable would show the withheld transcript.
        await Assert.That(states).DoesNotContain(SessionAccessState.Unavailable);
        await Assert.That(states[^1]).IsEqualTo(SessionAccessState.Denied);

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        h.Connect(epoch: 2);
        await WaitUntilAsync(async () => await Harness.Current(second) == SessionAccessState.Established, "established");
    }

    /// An observer runs with none of the service's locks held, so one that blocks, or calls back in
    /// from another thread, cannot stall every other lease.
    [Test]
    public async Task A_blocked_observer_holds_no_lock_the_service_needs() {
        var limit = TimeSpan.FromSeconds(30);
        using var h = new Harness();
        using var lease = h.Service.Acquire("s1");
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var left = new ManualResetEventSlim();
        using var watch = lease.State.Subscribe(s => {
            if (s != SessionAccessState.Established) return;
            entered.Set();
            resume.Wait();
            left.Set();
        });
        // Delivery can land on the thread that connects, and the observer blocks whichever thread it runs on.
        var connect = Task.Run(() => h.Connect());

        try {
            await Assert.That(await Task.Run(() => entered.Wait(limit))).IsTrue();

            var other = await Task.Run(() => {
                using var l = h.Service.Acquire("s2");
                h.Service.RecheckDenied("s1");
                return l.SessionId;
            }).WaitAsync(limit);

            await Assert.That(other).IsEqualTo("s2");
        } finally {
            // A failure has to free the observer too, or the cleanup below parks behind it and the suite hangs.
            resume.Set();
            await Task.Run(() => left.Wait(entered.IsSet ? limit : TimeSpan.Zero));
            await connect.WaitAsync(limit);
        }
    }

    /// A throwing observer must not wedge delivery: a later refusal still reaches the lease.
    [Test]
    public async Task A_throwing_observer_does_not_stop_later_states() {
        using var h = new Harness();
        using var lease = h.Service.Acquire("s1");
        using var thrower = lease.State.Subscribe(s => {
            if (s == SessionAccessState.Established) throw new InvalidOperationException("observer bug");
        });
        h.Connect();
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "established");

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");

        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "the refusal delivered");
    }

    [Test]
    public async Task Lane_down_is_unavailable_and_reconnect_re_establishes() {
        using var h = new Harness();
        var lease = h.Service.Acquire("s1");
        await Assert.That(await Harness.Current(lease)).IsEqualTo(SessionAccessState.Unavailable);
        h.Connect();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established after connect");
        h.Drop();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable on drop");
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Count(s => s == "s1") == 2, what: "re-subscribed after reconnect");
    }

    /// The set the rail reads to leave an open session's pending items to its own cards: in while
    /// access stands, out once it is refused or the last lease goes.
    [Test]
    public async Task Established_sessions_track_access_from_grant_to_denial_and_release() {
        using var h = new Harness();
        IReadOnlySet<string> open = new HashSet<string>();
        using var sub = h.Service.EstablishedSessions.Subscribe(s => open = s);
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => open.Contains("s1"), what: "established session listed");

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied");
        await Assert.That(open).DoesNotContain("s1");

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        h.Service.RecheckDenied("s1");
        await WaitUntilAsync(() => open.Contains("s1"), what: "re-granted session listed");

        lease.Dispose();
        await WaitUntilAsync(() => !open.Contains("s1"), what: "released session dropped");
    }

    [Test]
    public async Task Access_changed_ping_rechecks_and_a_denial_moves_the_lease_to_denied() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied after recheck");
    }

    [Test]
    public async Task A_transient_failure_retries_on_the_ladder_while_connected() {
        using var h = new Harness();
        var attempts = 0;
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(++attempts == 1 ? HubCallOutcome.Failed("recheck") : HubCallOutcome.Ok);
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable after the failure");

        // The ladder arms its timer just AFTER publishing Unavailable, so a single advance placed
        // between the two fires nothing and the retry never comes. Advancing a slice on each poll
        // is idempotent -- a no-op until the timer exists, and it fires once the rung is covered.
        var advanced = TimeSpan.Zero;
        await WaitUntilAsync(async () => {
            h.Time.Advance(Slice);
            advanced += Slice;
            return await Harness.Current(lease) == SessionAccessState.Established;
        }, "established on retry");

        // What it cost to get there is the rung: the first is two seconds and the next is five, so
        // a retry demoted to any later rung could not have arrived inside this budget.
        await Assert.That(advanced).IsLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>Small enough that the two-second first rung and the five-second second one land on
    /// different polls, so the budget above can tell them apart.</summary>
    static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(250);

    [Test]
    public async Task Two_leases_share_one_subscription_and_unsubscribe_on_the_last_release() {
        using var h = new Harness();
        h.Connect();
        var a = h.Service.Acquire("s1");
        var b = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(b) == SessionAccessState.Established, "established");
        await Assert.That(h.Lane.ChatSubscribes.Count(s => s == "s1")).IsEqualTo(1);
        a.Dispose();
        await Task.Delay(50);
        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
        b.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }

    /// The give-back is ordered behind the join it undoes, so the membership ends up dropped
    /// whichever way the two hub calls would otherwise have raced each other to the server.
    [Test]
    public async Task Lease_disposed_while_chat_subscribe_in_flight_still_unsubscribes() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>();
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "chat subscribe started");
        lease.Dispose();
        gate.SetResult(HubCallOutcome.Ok);
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "the membership given back once the subscribe resolved");
    }

    /// Closing and reopening the same workspace while the first attempt is still in flight. The
    /// stale attempt did join, and handing that group back while the reopened lease receives
    /// payloads on it leaves the lease Established, with no retry armed, while nothing arrives.
    /// Ordering the calls is what settles it: by the time the stale give-back can run, the
    /// reopened entry holds the session and neither give-back applies.
    [Test]
    public async Task A_stale_attempt_leaves_the_chat_a_live_lease_holds_alone() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var first = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "the first chat subscribe");

        first.Dispose();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        using var live = h.Service.Acquire("s1");
        gate.SetResult(HubCallOutcome.Ok);

        await WaitUntilAsync(async () => await Harness.Current(live) == SessionAccessState.Established, "the reopened lease established");
        await Task.Delay(100); // the stale attempt's give-back, were it not dropped, lands here

        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
        await Assert.That(await Harness.Current(live)).IsEqualTo(SessionAccessState.Established);
    }

    /// The same race on the release path: the last lease goes while a fresh acquisition is
    /// establishing, and the release's own give-back must find the session claimed again and
    /// leave the membership alone. The first attempt never joined, so nothing else can hand it
    /// back and the session has to end up subscribed.
    [Test]
    public async Task A_release_racing_a_fresh_acquisition_leaves_the_session_subscribed() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var first = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "the first chat subscribe");

        first.Dispose();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        using var live = h.Service.Acquire("s1");
        gate.SetResult(HubCallOutcome.Failed("closed"));

        await WaitUntilAsync(async () => await Harness.Current(live) == SessionAccessState.Established, "the reopened lease established");
        await Task.Delay(100); // the release's give-back, were it not dropped, lands here

        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
    }

    /// A hub failure is otherwise indistinguishable from the lane being down: the state is
    /// Unavailable either way, and the retry ladder keeps trying in silence. The reason is
    /// reported once per entry, not once per retry tick.
    [Test]
    [NotInParallel]
    public async Task A_failed_subscribe_reports_the_reason_once() {
        using var capture = ConsoleOutput.StartErrorCapture(newLine: "\n");
        using var h = new Harness();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Failed("boom"));
        h.Connect();
        using var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable after the failure");

        // Same arming gap as the ladder test: advance on each poll rather than once. Every attempt
        // fails here, so a later rung can arm and fire too -- the count is a floor, and the dedup
        // this test is about holds however many retries ran, since the reason never changes.
        await WaitUntilAsync(() => {
            h.Time.Advance(TimeSpan.FromSeconds(2));
            return h.Lane.ChatSubscribes.Count(s => s == "s1") >= 2;
        }, what: "the retry");
        await Task.Delay(100); // the second attempt's report, were it not deduped, lands here

        var lines = capture.GetCapturedError().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("s1").And.Contains("boom");
    }

    [Test]
    public async Task Dispose_ignores_late_lane_notifications_and_completes_leases() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        var callsBeforeDispose = h.Lane.Calls.Count;
        h.Service.Dispose();

        var completed = false;
        using (lease.State.Subscribe(_ => { }, () => completed = true)) { }
        await Assert.That(completed).IsTrue();

        h.Connect(epoch: 2);
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await Task.Delay(50);
        await Assert.That(h.Lane.Calls.Count).IsEqualTo(callsBeforeDispose);
    }
}
