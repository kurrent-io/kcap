using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections.Concurrent;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using TUnit.Assertions.Enums;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class MacNotificationBadgeLifecycleTests {
    static DesktopNotification Notice(string id) => new(id, "Permission requested", "Agent needs you", [new("allow", "Allow")]);

    [Test]
    public async Task Native_dismissal_open_and_actions_update_the_count_without_changing_action_ids() {
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var counts = new List<int>();
        var actions = new List<string?>();
        using var watch = sink.BadgeCounts.Subscribe(counts.Add);
        sink.Show(Notice("a"), actions.Add);
        sink.Show(Notice("b"), actions.Add);
        await Assert.That(native.Published.Select(p => p.Badge)).IsEquivalentTo([(int?)1, 2], CollectionOrdering.Matching);
        native.Respond(native.Published[0].NativeId, "com.apple.UNNotificationDismissActionIdentifier");
        await Assert.That(actions).IsEmpty();
        native.Respond(native.Published[1].NativeId, "category.allow");
        await Assert.That(actions).IsEquivalentTo(new string?[] { "allow" });
        sink.Show(Notice("c"), actions.Add);
        native.Respond(native.Published[2].NativeId, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(actions).IsEquivalentTo(["allow", null], CollectionOrdering.Matching);
        await Assert.That(counts).IsEquivalentTo([0, 1, 2, 1, 0, 1, 0], CollectionOrdering.Matching);
        await Assert.That(native.Badges.Last().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Replacing_an_id_does_not_count_twice_or_let_an_old_native_response_open_it() {
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        var opened = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => opened++);
        var old = native.Published.Single().NativeId;
        sink.Show(Notice("a"), _ => opened++);
        native.Respond(old, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(opened).IsEqualTo(0);
        sink.Close("a");
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Released).Count().IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Activation_or_foreground_suppression_withdraws_an_add_that_finishes_late(bool suppressed) {
        var native = new FakeMacNotificationCenter { CompleteAdds = false };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => throw new InvalidOperationException("No activation expected"));
        var add = native.Published.Single();
        if (suppressed) native.Suppress(add.NativeId);
        else sink.OnActivated();
        add.Complete(null);
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Withdrawn.Count(id => id == add.NativeId)).IsEqualTo(2);
        await Assert.That(native.Badges.Any(b => b.Count != 0)).IsFalse();
        await Assert.That(native.Badges).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Activation_while_authorization_waits_does_not_publish_after_the_answer() {
        var authorization = new TaskCompletionSource();
        var native = new FakeMacNotificationCenter { Request = () => authorization.Task };
        var posted = new ConcurrentQueue<Action>();
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = false;
        using var sink = new MacOsDesktopNotificationSink(native, action => {
            posted.Enqueue(action);
            if (waiting) resumed.TrySetResult();
        }, new FakeTimeProvider());
        while (posted.TryDequeue(out var initial)) initial();
        sink.Show(Notice("a"), _ => { });
        sink.OnActivated();
        waiting = true;
        authorization.SetResult();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (posted.TryDequeue(out var action)) action();
        await Assert.That(native.Published).IsEmpty();
    }

    [Test]
    public async Task Denied_badges_keep_alerts_without_retaining_a_Dock_count() {
        var native = new FakeMacNotificationCenter { Settings = (DesktopNotificationAccess.Allowed, false) };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        await Assert.That(native.Published.Single().Badge).IsNull();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(await sink.NeedsRequestAsync()).IsTrue();
        native.Request = () => { native.Settings = (DesktopNotificationAccess.Allowed, true); return Task.CompletedTask; };
        await sink.RequestAsync();
        await Assert.That(count).IsEqualTo(1);
        native.Settings = (DesktopNotificationAccess.Denied, false);
        native.Request = null;
        await sink.GetAsync();
        await Assert.That(count).IsEqualTo(0);
        sink.Show(Notice("b"), _ => { });
        await Assert.That(native.Published).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Badge_writes_are_serialized_and_finish_at_zero_after_withdrawal() {
        var native = new FakeMacNotificationCenter { CompleteBadges = false };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        sink.Show(Notice("a"), _ => { });
        sink.Show(Notice("b"), _ => { });
        await Assert.That(native.Badges).Count().IsEqualTo(1);
        native.Badges[0].Complete(true);
        await Assert.That(native.Badges.Last().Count).IsEqualTo(2);
        sink.OnActivated();
        native.Badges[1].Complete(true);
        await Assert.That(native.Badges.Last().Count).IsEqualTo(0);
        native.Badges[2].Complete(true);
        await Assert.That(native.Badges.Select(b => b.Count)).IsEquivalentTo([0, 2, 0], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_badge_error_releases_retention_and_attempts_one_native_clear() {
        var native = new FakeMacNotificationCenter();
        native.BadgeResults.Enqueue(true);
        native.BadgeResults.Enqueue(false);
        native.BadgeResults.Enqueue(false);
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Badges.Select(b => b.Count)).IsEquivalentTo([0, 1, 0], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Native_add_failure_never_creates_a_pending_badge() {
        var native = new FakeMacNotificationCenter { CompleteAdds = false };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        native.Published.Single().Complete("Delivery failed");
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Released).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Notification_preferences_and_resolved_permissions_clear_the_native_badge() {
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        using var permissions = new FakePermissionService();
        using var directory = new FakeAgentDirectory();
        using var preferences = new BehaviorSubject<NotificationPreferences>(new());
        using var viewed = new Subject<string?>();
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto("a1", "agent", "claude", "/repo", "Running", null, null, null,
            DateTime.UtcNow, null, null, SessionId: "s1", AwaitingInput: false), new RepoIdentity("repo", "repo")));
        using var coordinator = new DesktopNotificationCoordinator(permissions, directory, preferences, sink,
            () => false, viewed, _ => { }, new AppNotifier(), ImmediateScheduler.Instance);
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        permissions.Add(PermissionEntries.Entry());
        await Assert.That(count).IsEqualTo(1);
        preferences.OnNext(new(false, false, false));
        await Assert.That(count).IsEqualTo(0);
        preferences.OnNext(new());
        permissions.Add(PermissionEntries.Entry("r2"));
        await Assert.That(count).IsEqualTo(1);
        permissions.Remove("r2");
        await Assert.That(count).IsEqualTo(0);
        permissions.Add(PermissionEntries.Entry("r3"));
        await Assert.That(count).IsEqualTo(1);
        viewed.OnNext("local:a1");
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Native_clear_all_and_settings_revocation_are_reconciled_without_a_delegate_callback() {
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        await sink.ReconcileAsync();
        native.ReadDelivered = () => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>());
        await sink.ReconcileAsync();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Withdrawn).Count().IsEqualTo(1);
        native.ReadDelivered = null;
        sink.Show(Notice("b"), _ => { });
        await Assert.That(count).IsEqualTo(1);
        native.Settings = (DesktopNotificationAccess.Allowed, false);
        await sink.ReconcileAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task A_delivery_grace_prevents_an_early_snapshot_from_dropping_a_new_notice_then_stops_polling() {
        var time = new FakeTimeProvider();
        var native = new FakeMacNotificationCenter {
            ReadDelivered = () => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>()),
        };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), time);
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        await sink.ReconcileAsync();
        await Assert.That(count).IsEqualTo(1);
        time.Advance(MacOsDesktopNotificationSink.DeliveryGrace);
        await Assert.That(count).IsEqualTo(0);
        var reads = native.DeliveredReads;
        time.Advance(MacOsDesktopNotificationSink.DeliveryGrace);
        await Assert.That(native.DeliveredReads).IsEqualTo(reads);
    }

    [Test]
    public async Task Reconciliation_has_one_native_read_and_a_late_snapshot_cannot_restore_an_activated_notice() {
        var delivered = new TaskCompletionSource<IReadOnlySet<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeMacNotificationCenter { ReadDelivered = () => delivered.Task };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        var first = sink.ReconcileAsync();
        await sink.ReconcileAsync();
        await Assert.That(native.DeliveredReads).IsEqualTo(1);
        sink.OnActivated();
        delivered.SetResult(native.Published.Select(p => p.NativeId).ToHashSet(StringComparer.Ordinal));
        await first;
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Badges.Last().Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_native_snapshot_timeout_releases_Dock_retention_and_allows_the_next_reconciliation() {
        var time = new FakeTimeProvider();
        var never = new TaskCompletionSource<IReadOnlySet<string>?>();
        var native = new FakeMacNotificationCenter { ReadDelivered = () => never.Task };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), time);
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        var reconciliation = sink.ReconcileAsync();
        await Assert.That(count).IsEqualTo(1);
        time.Advance(TimeSpan.FromSeconds(5));
        await reconciliation;
        await Assert.That(count).IsEqualTo(0);
        native.ReadDelivered = null;
        await sink.ReconcileAsync();
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(native.DeliveredReads).IsEqualTo(2);
    }

    [Test]
    public async Task Native_callbacks_are_dispatched_before_mutating_badge_state() {
        var posted = new ConcurrentQueue<Action>();
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, posted.Enqueue, new FakeTimeProvider());
        while (posted.TryDequeue(out var initial)) initial();
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => { });
        while (posted.TryDequeue(out var publish)) publish();
        await Assert.That(count).IsEqualTo(1);
        await Task.Run(() => native.Respond(native.Published.Single().NativeId, "com.apple.UNNotificationDismissActionIdentifier"));
        await Assert.That(count).IsEqualTo(1);
        while (posted.TryDequeue(out var dismiss)) dismiss();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Activation_before_a_native_open_response_clears_the_badge_and_still_routes_the_agent() {
        var native = new FakeMacNotificationCenter();
        var time = new FakeTimeProvider();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), time);
        var count = 0;
        var actions = new List<string?>();
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), actions.Add);
        var nativeId = native.Published.Single().NativeId;
        sink.OnActivated();
        await Assert.That(count).IsEqualTo(0);
        native.Respond(nativeId, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(actions).Count().IsEqualTo(1);
        await Assert.That(actions[0]).IsNull();
        native.Respond(nativeId, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(actions).Count().IsEqualTo(1);

        sink.Show(Notice("b"), actions.Add);
        var old = native.Published.Last().NativeId;
        sink.OnActivated();
        time.Advance(MacOsDesktopNotificationSink.ResponseGrace + TimeSpan.FromSeconds(1));
        native.Respond(old, "category.allow");
        await Assert.That(actions).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Disposal_queues_a_final_zero_after_an_in_flight_native_badge_write() {
        var native = new FakeMacNotificationCenter { CompleteBadges = false };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        native.Badges[0].Complete(true);
        sink.Show(Notice("a"), _ => { });
        await Assert.That(native.Badges.Last().Count).IsEqualTo(1);
        sink.Dispose();
        native.Badges[1].Complete(true);
        await Assert.That(native.Badges.Last().Count).IsEqualTo(0);
        native.Badges[2].Complete(true);
        await Assert.That(native.Withdrawn).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments("com.apple.UNNotificationDefaultActionIdentifier", null)]
    [Arguments("category.allow", "allow")]
    public async Task A_native_response_before_add_completion_still_routes_once_after_activation(string nativeAction, string? expected) {
        var native = new FakeMacNotificationCenter { CompleteAdds = false };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var actions = new List<string?>();
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), actions.Add);
        var add = native.Published.Single();
        sink.OnActivated();
        await Assert.That(count).IsEqualTo(0);
        native.Respond(add.NativeId, nativeAction);
        native.Respond(add.NativeId, nativeAction);
        add.Complete(null);
        await Assert.That(actions).Count().IsEqualTo(1);
        await Assert.That(actions[0]).IsEqualTo(expected);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task A_synchronous_publish_failure_releases_its_registration_and_category() {
        var native = new FakeMacNotificationCenter { PublishFailure = new InvalidOperationException("Native publish failed") };
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => throw new InvalidOperationException("No native notice exists"));
        sink.OnActivated();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(native.Withdrawn).Count().IsEqualTo(1);
        await Assert.That(native.Released).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Replacement_and_disposal_invalidate_activation_response_grace() {
        var native = new FakeMacNotificationCenter();
        using var sink = new MacOsDesktopNotificationSink(native, action => action(), new FakeTimeProvider());
        var opened = 0;
        var count = 0;
        using var watch = sink.BadgeCounts.Subscribe(value => count = value);
        sink.Show(Notice("a"), _ => opened++);
        var old = native.Published.Last().NativeId;
        sink.OnActivated();
        sink.Show(Notice("a"), _ => opened++);
        native.Respond(old, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(opened).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(1);
        var current = native.Published.Last().NativeId;
        sink.Dispose();
        native.Respond(current, "com.apple.UNNotificationDefaultActionIdentifier");
        await Assert.That(opened).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }
}
