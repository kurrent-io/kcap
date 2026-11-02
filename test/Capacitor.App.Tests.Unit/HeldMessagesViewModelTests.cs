using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The held list: read only while the server sends rights, re-read on this agent's push, decided by the owner and
/// withdrawn by the sender.
[NotInParallel(nameof(AvaloniaSession))]
public class HeldMessagesViewModelTests {
    static readonly Guid First = Guid.Parse("11111111-0000-0000-0000-000000000001");

    static AgentViewerAccess Owner => AgentViewerAccess.From(new AgentViewerRightsDto {
        IsOwner = true, Reaches = true, AnswerQuestions = true, ApproveToolUse = true, SendWithoutApproval = true, HoldMessages = true,
    });

    static AgentViewerAccess Sender => AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = true, HoldMessages = true });

    [Test]
    public async Task No_rights_from_the_server_read_nothing_and_hold_nothing() {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi();
            var changed = new Subject<string>();
            using var held = new HeldMessagesViewModel("a1", api, changed, Observable.Return(AgentViewerAccess.Unrestricted));
            held.Refresh();
            changed.OnNext("a1");
            await Assert.That(api.Reads).IsEmpty();
            await Assert.That(held.HasItems).IsFalse();
        });
    }

    [Test]
    public async Task This_agents_push_reads_the_list_again_and_another_agents_does_not() {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi { ListHandler = _ => Task.FromResult(new HeldMessagesRead([FakeHeldMessagesApi.Message(First)])) };
            var changed = new Subject<string>();
            using var held = new HeldMessagesViewModel("a1", api, changed, Observable.Return(Owner));
            await WaitUntilAsync(() => held.HasItems, what: "the first read");
            await Assert.That(api.Reads.Count).IsEqualTo(1);

            changed.OnNext("a2");
            changed.OnNext("a1");
            await WaitUntilAsync(() => api.Reads.Count == 2, what: "the re-read");
            await Assert.That(api.Reads.Count).IsEqualTo(2);
        });
    }

    /// A failed read keeps the list it had; a 404-shaped read (nothing held) clears it.
    [Test]
    public async Task A_failed_read_keeps_the_last_list() {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi { ListHandler = _ => Task.FromResult(new HeldMessagesRead([FakeHeldMessagesApi.Message(First)])) };
            using var held = new HeldMessagesViewModel("a1", api, new Subject<string>(), Observable.Return(Owner));
            await WaitUntilAsync(() => held.HasItems, what: "the first read");

            api.ListHandler = _ => Task.FromResult(HeldMessagesRead.Failed);
            held.Refresh();
            await WaitUntilAsync(() => api.Reads.Count == 2, what: "the failed read");
            await Assert.That(held.HasItems).IsTrue();

            api.ListHandler = _ => Task.FromResult(HeldMessagesRead.None);
            held.Refresh();
            await WaitUntilAsync(() => !held.HasItems, what: "the empty read");
        });
    }

    [Test]
    [Arguments(true, "Bea", "Waiting for your approval", true, false)]
    [Arguments(false, "You", "Waiting for the owner's approval", false, true)]
    public async Task The_owner_decides_and_the_sender_withdraws(bool owner, string sender, string status, bool decides, bool withdraws) {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi { ListHandler = _ => Task.FromResult(new HeldMessagesRead([FakeHeldMessagesApi.Message(First)])) };
            using var held = new HeldMessagesViewModel("a1", api, new Subject<string>(), Observable.Return(owner ? Owner : Sender));
            await WaitUntilAsync(() => held.HasItems, what: "the read");
            var item = held.Items.Single();
            await Assert.That(item.Sender).IsEqualTo(sender);
            await Assert.That(item.StatusText).IsEqualTo(status);
            await Assert.That(item.CanDecide).IsEqualTo(decides);
            await Assert.That(item.CanWithdraw).IsEqualTo(withdraws);
            await Assert.That(await item.ApproveCommand.CanExecute.FirstAsync()).IsEqualTo(decides);
            await Assert.That(await item.WithdrawCommand.CanExecute.FirstAsync()).IsEqualTo(withdraws);

            await (owner ? item.ApproveCommand : item.WithdrawCommand).Execute().ToTask();
            await Assert.That(api.Decisions.Single()).IsEqualTo((First, owner ? HeldMessageDecision.Approve : HeldMessageDecision.Withdraw));
            await WaitUntilAsync(() => api.Reads.Count == 2, what: "the read after the decision");
        });
    }

    [Test]
    public async Task A_refused_decision_shows_why() {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi {
                ListHandler = _ => Task.FromResult(new HeldMessagesRead([FakeHeldMessagesApi.Message(First)])),
                DecideHandler = _ => Task.FromResult(new HeldMessageDecisionOutcome(HeldMessageDecisionKind.AlreadyDecided)),
            };
            using var held = new HeldMessagesViewModel("a1", api, new Subject<string>(), Observable.Return(Owner));
            await WaitUntilAsync(() => held.HasItems, what: "the read");
            await held.Items.Single().RejectCommand.Execute().ToTask();
            await Assert.That(held.ErrorText).IsEqualTo("That message was already decided");
        });
    }

    [Test]
    [Arguments("dispatched", "sent", "Sent")]
    [Arguments("dispatched", "not_delivered", "Not delivered")]
    [Arguments("dispatched", null, "Sending")]
    [Arguments("approved", null, "Sending")]
    [Arguments("rejected", null, "Rejected")]
    [Arguments("withdrawn", null, "Withdrawn")]
    [Arguments("discarded", null, "Discarded")]
    public async Task A_decided_message_shows_where_it_stands_and_offers_nothing(string state, string? outcome, string status) {
        await RunOnUiAsync(async () => {
            var api = new FakeHeldMessagesApi { ListHandler = _ => Task.FromResult(new HeldMessagesRead([FakeHeldMessagesApi.Message(First, state, outcome)])) };
            using var held = new HeldMessagesViewModel("a1", api, new Subject<string>(), Observable.Return(Owner));
            await WaitUntilAsync(() => held.HasItems, what: "the read");
            var item = held.Items.Single();
            await Assert.That(item.StatusText).IsEqualTo(status);
            await Assert.That(item.CanDecide).IsFalse();
            await Assert.That(item.CanWithdraw).IsFalse();
        });
    }
}
