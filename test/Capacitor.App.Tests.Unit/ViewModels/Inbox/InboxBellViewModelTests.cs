using System.Reactive.Linq;
using RxUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services.Inbox;
using Capacitor.App.ViewModels.Inbox;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.ViewModels.Inbox;

[NotInParallel("AvaloniaSession")]
public class InboxBellViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    static NotificationItemDto Item(string id, long version = 1, bool unread = true, string type = "pass_received") => new() {
        LogicalId = id, Version = version, IsUnread = unread, Type = type, Title = $"title {id}", UpdatedAt = Now.AddMinutes(-5),
        LinkUrl = "/passes/p1", LinkLabel = "Open pass",
    };

    static InboxRead<NotificationPageDto> PageOf(int unread, params NotificationItemDto[] items) =>
        new(InboxReadKind.Ready, new() { Items = items, UnreadCount = unread });

    sealed class Rig : IDisposable {
        public readonly FakeInboxApi Api = new();
        public readonly BehaviorSubject<ServerLaneStatus> Status = new(new(ServerLaneState.Dormant));
        public readonly Subject<RxUnit> Changed = new();
        public readonly List<string> Opened = [];
        public int SettingsOpened;
        public InboxBellViewModel Bell = null!;

        public Rig Start() {
            Bell = new(Api, Status, Changed, Opened.Add, () => SettingsOpened++, new FakeTimeProvider(Now));
            return this;
        }

        public void Connect() => Status.OnNext(new(ServerLaneState.Connected));

        public void Dispose() => Bell.Dispose();
    }

    [Test]
    public async Task The_bell_shows_once_connected_where_notifications_are_offered_and_names_its_count() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Api.Count = new(InboxReadKind.Ready, new() { Count = 120 });
        await Assert.That(rig.Bell.IsVisible).IsFalse();

        rig.Connect();

        await Assert.That(rig.Bell.IsVisible).IsTrue();
        await Assert.That(rig.Bell.BadgeText).IsEqualTo("99+");
        await Assert.That(rig.Bell.AccessibleName).IsEqualTo("Notifications, 120 unread");

        rig.Status.OnNext(new(ServerLaneState.SignedOut));
        await Assert.That(rig.Bell.IsVisible).IsFalse();
    });

    [Test]
    public async Task The_bell_stays_hidden_where_notifications_are_off_or_the_server_lacks_the_routes() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var off = new Rig().Start();
        off.Api.Surfaces = new(InboxReadKind.Ready, new() { Notifications = false });
        off.Connect();
        await Assert.That(off.Bell.IsVisible).IsFalse();
        await Assert.That(off.Api.CountReads).IsEqualTo(0);

        using var old = new Rig().Start();
        old.Api.Count = InboxRead<NotificationUnreadCountDto>.Unavailable;
        old.Connect();
        await Assert.That(old.Bell.IsVisible).IsFalse();
    });

    [Test]
    public async Task A_closed_panel_reads_only_the_count_and_opening_it_reads_the_list() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Api.Page = PageOf(1, Item("n1"));
        rig.Connect();
        rig.Changed.OnNext(RxUnit.Default);
        await Assert.That(rig.Api.PageReads).IsEqualTo(0);
        await Assert.That(rig.Api.CountReads).IsEqualTo(2);

        rig.Bell.IsOpen = true;

        await Assert.That(rig.Api.PageReads).IsEqualTo(1);
        await Assert.That(rig.Bell.Items.Single().LinkUrl).IsEqualTo("https://kcap.example/passes/p1");
        await Assert.That(rig.Bell.UnreadCount).IsEqualTo(1);
        await Assert.That(rig.Bell.IsLoading).IsFalse();
    });

    [Test]
    public async Task Pushes_that_land_during_a_read_cost_one_more_read() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Connect();
        var gate = new TaskCompletionSource();
        rig.Api.CountGate = gate;
        rig.Changed.OnNext(RxUnit.Default);
        rig.Changed.OnNext(RxUnit.Default);
        rig.Changed.OnNext(RxUnit.Default);
        rig.Api.CountGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(rig.Api.CountReads).IsEqualTo(3);
    });

    [Test]
    public async Task A_dismissed_row_stays_hidden_until_the_server_shows_a_newer_version() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Api.Page = PageOf(2, Item("n1"), Item("n2"));
        rig.Connect();
        rig.Bell.IsOpen = true;

        await rig.Bell.Items[0].DismissCommand.Execute();

        await Assert.That(rig.Api.Calls).Contains("dismiss n1@1");
        await Assert.That(rig.Bell.Items.Select(i => i.LogicalId)).IsEquivalentTo(["n2"]);
        await Assert.That(rig.Bell.UnreadCount).IsEqualTo(1);

        rig.Api.Page = PageOf(2, Item("n1", version: 2), Item("n2"));
        rig.Bell.Refresh();
        await Assert.That(rig.Bell.Items.Select(i => i.LogicalId)).IsEquivalentTo(["n1", "n2"]);
    });

    [Test]
    public async Task An_answer_shows_the_servers_sentence_or_a_fallback_when_it_could_not_be_sent() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        var prompt = Item("n1", type: "agent_prompt") with {
            Buttons = [new() { Answer = "allow", Label = "Allow", Style = "primary" }], ReplyLabel = "Reply",
        };
        rig.Api.Page = PageOf(1, prompt);
        rig.Connect();
        rig.Bell.IsOpen = true;
        var card = rig.Bell.Items.Single();

        rig.Api.Answer = new(InboxReadKind.Ready, new() { Accepted = true, Message = "Approved" });
        card.Buttons.Single().Command.Execute(null);
        await Assert.That(card.Note).IsEqualTo("Approved");

        rig.Api.Answer = InboxRead<NotificationAnswerResponseDto>.Failed;
        card.ReplyText = "on it";
        await card.SendReplyCommand.Execute();
        await Assert.That(rig.Api.Calls).Contains("answer n1 reply on it");
        await Assert.That(card.Note).IsEqualTo("Couldn't send that answer. Try again, or answer it in kcap");
        await Assert.That(card.ReplyText).IsNull();
    });

    [Test]
    public async Task A_read_begun_before_a_disconnect_never_shows_the_bell() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        var gate = new TaskCompletionSource();
        rig.Api.CountGate = gate;
        rig.Api.Count = new(InboxReadKind.Ready, new() { Count = 4 });
        rig.Connect();
        rig.Status.OnNext(new(ServerLaneState.Retrying));

        gate.SetResult();
        await Task.Yield();

        await Assert.That(rig.Bell.IsVisible).IsFalse();
        await Assert.That(rig.Bell.UnreadCount).IsEqualTo(0);
    });

    [Test]
    public async Task A_reconnect_during_a_running_read_still_admits_the_bell() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        var gate = new TaskCompletionSource();
        rig.Api.CountGate = gate;
        rig.Api.Count = new(InboxReadKind.Ready, new() { Count = 2 });
        rig.Connect();
        rig.Status.OnNext(new(ServerLaneState.Retrying));
        rig.Connect();

        rig.Api.CountGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(rig.Bell.IsVisible).IsTrue();
        await Assert.That(rig.Bell.UnreadCount).IsEqualTo(2);
    });

    [Test]
    public async Task A_re_read_keeps_the_answer_sentence_and_an_unsent_reply_on_the_replacement_card() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        var prompt = Item("n1", type: "agent_prompt") with {
            Buttons = [new() { Answer = "allow", Label = "Allow", Style = "primary" }], ReplyLabel = "Reply",
        };
        rig.Api.Page = PageOf(1, prompt, Item("n2"));
        rig.Api.Answer = new(InboxReadKind.Ready, new() { Accepted = true, Message = "Approved" });
        rig.Connect();
        rig.Bell.IsOpen = true;

        rig.Bell.Items[0].Buttons.Single().Command.Execute(null);
        rig.Bell.Items[1].ReplyText = "half typed";
        rig.Changed.OnNext(RxUnit.Default);

        await Assert.That(rig.Bell.Items[0].Note).IsEqualTo("Approved");
        await Assert.That(rig.Bell.Items[1].ReplyText).IsEqualTo("half typed");
    });

    [Test]
    public async Task An_answer_that_lands_after_a_disconnect_leaves_no_sentence_for_the_next_connection() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        var prompt = Item("n1", type: "agent_prompt") with { Buttons = [new() { Answer = "allow", Label = "Allow", Style = "primary" }] };
        rig.Api.Page = PageOf(1, prompt);
        rig.Api.Answer = new(InboxReadKind.Ready, new() { Accepted = true, Message = "Approved" });
        var gate = new TaskCompletionSource();
        rig.Api.AnswerGate = gate;
        rig.Connect();
        rig.Bell.IsOpen = true;

        rig.Bell.Items.Single().Buttons.Single().Command.Execute(null);
        rig.Status.OnNext(new(ServerLaneState.SignedOut));
        gate.SetResult();
        await Task.Yield();
        rig.Connect();
        rig.Bell.IsOpen = true;

        await Assert.That(rig.Bell.Items.Single().Note).IsNull();
    });

    [Test]
    public async Task Delete_all_acts_only_once_confirmed() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Api.Page = PageOf(1, Item("n1"));
        rig.Connect();
        rig.Bell.IsOpen = true;

        await rig.Bell.AskDeleteAllCommand.Execute();
        await rig.Bell.CancelDeleteAllCommand.Execute();
        await Assert.That(rig.Api.DismissAllCalls).IsEqualTo(0);

        await rig.Bell.AskDeleteAllCommand.Execute();
        await Assert.That(rig.Bell.IsConfirmingDeleteAll).IsTrue();
        await rig.Bell.DeleteAllCommand.Execute();
        await Assert.That(rig.Api.DismissAllCalls).IsEqualTo(1);
        await Assert.That(rig.Bell.IsConfirmingDeleteAll).IsFalse();
    });

    [Test]
    public async Task Following_a_link_or_opening_settings_closes_the_panel() => await AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig().Start();
        rig.Api.Page = PageOf(1, Item("n1"));
        rig.Connect();
        rig.Bell.IsOpen = true;

        await rig.Bell.Items.Single().OpenLinkCommand.Execute();
        await Assert.That(rig.Opened).IsEquivalentTo(["https://kcap.example/passes/p1"]);
        await Assert.That(rig.Bell.IsOpen).IsFalse();

        rig.Bell.IsOpen = true;
        await rig.Bell.OpenSettingsCommand.Execute();
        await Assert.That(rig.SettingsOpened).IsEqualTo(1);
        await Assert.That(rig.Bell.IsOpen).IsFalse();
    });
}
