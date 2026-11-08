using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// What the chat's queue shows of a message to an attached agent: the server lists where its delivery stands, and the
/// chat reads that rather than guessing from the transcript.
public class ChatDeliveryStateTests {
    const string Waiting = "Waiting for the turn to end";
    const string SendAgain = "Not delivered, send again";

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("held", null, Waiting)]
    [Arguments("with_mod", null, Waiting)]
    [Arguments("delivered", null, "Delivered")]
    [Arguments("rejected", null, SendAgain)]
    [Arguments("unconfirmed", null, "Delivery unknown, check the session before sending again")]
    [Arguments("unconfirmed", "attachment_lapsed", "Delivery unknown, check the session before sending again")]
    [Arguments("rejected", "stopped", "Not delivered, the session was stopped at the terminal")]
    [Arguments("rejected", "command", "Not delivered, commands can only be run at the terminal")]
    [Arguments("rejected", "unconfirmed", "Not delivered, an earlier message's delivery is still unknown")]
    [Arguments("rejected", "attachments_unsupported", "Not delivered, files can't be sent to a terminal session")]
    [Arguments("rejected", "session_changed", SendAgain)]
    [Arguments("rejected", "not_held_by_mod", SendAgain)]
    [Arguments("rejected", "dropped", SendAgain)]
    [Arguments("rejected", "refused", SendAgain)]
    [Arguments("rejected", "a_reason_this_build_does_not_know", SendAgain)]
    public Task Each_state_shows_its_copy(string state, string? reason, string copy) => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();
            h.Queue.OnNext([Item("next step", Guid.NewGuid(), state: state, reason: reason)]);

            if (copy == Waiting) {
                var waiting = h.Chat.QueuedMessages.Single();
                await Assert.That(waiting.DisplayText).IsEqualTo("next step");
                await Assert.That(waiting.PickupNote).IsEqualTo(copy);
                await Assert.That(waiting.ShowsForeignUnconfirmed).IsFalse();
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
                await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
            } else {
                await Assert.That(h.Chat.DeliveryNotices).Count().IsEqualTo(1);
                var notice = h.Chat.DeliveryNotices[0];
                await Assert.That(notice.DisplayText).IsEqualTo("next step");
                await Assert.That(notice.DeliveryNote).IsEqualTo(copy);
                await Assert.That(h.Chat.HasDeliveryNotices).IsTrue();
                await Assert.That(h.Chat.QueuedMessages).IsEmpty();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            }
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// Only a settled message leaves the queue: what still waits is counted, and nothing else is.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_settled_message_is_a_notice_and_not_counted_as_waiting() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();
            h.Queue.OnNext([
                Item("first", Guid.NewGuid(), state: "rejected", reason: "stopped"),
                Item("second", Guid.NewGuid(), state: "held"),
            ]);

            await Assert.That(h.Chat.QueuedMessages.Select(q => q.DisplayText)).IsEquivalentTo(["second"]);
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            await Assert.That(h.Chat.DeliveryNotices.Select(q => q.DisplayText)).IsEquivalentTo(["first"]);
        } finally { await h.Chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_hosted_agents_queued_message_shows_no_delivery_line() => RunOnUiAsync(async () => {
        var h = new Rig();
        try {
            await h.SeededAsync();
            h.Chat.ComposerText = "mine";
            await h.Chat.SendCommand.Execute();
            h.Queue.OnNext([Item("mine", Guid.NewGuid()), Item("theirs", Guid.NewGuid(), sender: "u3")]);

            var own = h.Chat.QueuedMessages.Single(q => q.Text == "mine");
            var foreign = h.Chat.QueuedMessages.Single(q => q.Text == "theirs");
            await Assert.That(own.DeliveryNote).IsNull();
            await Assert.That(own.PickupNote).IsEqualTo(QueuedChatMessage.UnknownPickupNote);
            await Assert.That(own.ShowsUnconfirmed).IsFalse();
            await Assert.That(foreign.DeliveryNote).IsNull();
            await Assert.That(foreign.PickupNote).IsNull();
            await Assert.That(foreign.ShowsForeignUnconfirmed).IsTrue();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued · 1 unconfirmed");
            await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// A state this build does not know is shown as a hosted agent's message is, with no delivery line.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_message_in_an_unknown_state_shows_as_a_hosted_one() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();
            h.Queue.OnNext([Item("theirs", Guid.NewGuid(), sender: "u3", state: "parked_somewhere_new", reason: "why")]);

            var row = h.Chat.QueuedMessages.Single();
            await Assert.That(row.DeliveryNote).IsNull();
            await Assert.That(row.PickupNote).IsNull();
            await Assert.That(row.ShowsForeignUnconfirmed).IsTrue();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
            await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// The sender's own message reads the server's state like anyone's, and once the server stops listing a message it
    /// listed with a state, the message is over: it does not come back as an unconfirmed send to retry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_own_message_follows_its_delivery_and_leaves_once_the_server_drops_it() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();
            h.Chat.ComposerText = "mine";
            await h.Chat.SendCommand.Execute();
            var own = h.Chat.QueuedMessages.Single();
            var id = Guid.NewGuid();

            h.Queue.OnNext([Item("mine", id, state: "held")]);
            await Assert.That(own.PickupNote).IsEqualTo(Waiting);

            h.Queue.OnNext([Item("mine", id, state: "rejected", reason: "stopped")]);
            await Assert.That(h.Chat.QueuedMessages).IsEmpty();
            await Assert.That(h.Chat.DeliveryNotices).Count().IsEqualTo(1);
            await Assert.That(h.Chat.DeliveryNotices[0]).IsSameReferenceAs(own);
            await Assert.That(own.DeliveryNote).IsEqualTo("Not delivered, the session was stopped at the terminal");
            await Assert.That(own.CanSendNow).IsFalse();

            h.Queue.OnNext([]);
            await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
            await Assert.That(h.Chat.QueuedMessages).IsEmpty();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// A delivered message the server stops listing is over, and so is every attempt it replaced: an earlier attempt
    /// does not come back as a message still waiting.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_delivered_retry_takes_the_attempt_it_replaced_with_it() => RunOnUiAsync(async () => {
        using var identity = new BehaviorSubject<ViewerIdentityDto?>(null);
        var h = new Rig(attached: true, identity: identity);
        try {
            await h.SeededAsync();
            h.Chat.ComposerText = "mine";
            await h.Chat.SendCommand.Execute();
            var first = h.Chat.QueuedMessages.Single();
            await WaitUntilAsync(() => first.CanSendNow, what: "the first attempt resendable");

            identity.OnNext(new() { UserId = "u2" });
            await h.Chat.SendQueuedCommand.Execute(first);
            var retry = h.Chat.QueuedMessages.Single();
            await Assert.That(retry).IsNotSameReferenceAs(first);
            var id = Guid.NewGuid();
            h.Queue.OnNext([Item("mine", id, state: "with_mod")]);
            h.Queue.OnNext([Item("mine", id, state: "delivered")]);
            await Assert.That(h.Chat.DeliveryNotices.Single()).IsSameReferenceAs(retry);

            h.Queue.OnNext([]);

            await Assert.That(h.Chat.QueuedMessages).IsEmpty();
            await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// The server cancels a discarded message it still holds for an attached agent, so only that one says a discard
    /// cancels it. One already handed to the session, and a hosted agent's, can't be recalled.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, "held", true)]
    [Arguments(true, "with_mod", false)]
    [Arguments(false, null, false)]
    public Task A_discard_cancels_only_a_message_the_server_still_holds_for_an_attached_agent(bool attached, string? state, bool cancels) =>
        RunOnUiAsync(async () => {
            var h = new Rig(attached);
            try {
                await h.SeededAsync();
                h.Queue.OnNext([Item("theirs", Guid.NewGuid(), sender: "u3", state: state)]);

                await Assert.That(h.Chat.QueuedMessages.Single().DiscardTip)
                    .IsEqualTo(cancels ? AttachedAgentCopy.DiscardCancels : QueuedChatMessage.NotCancelledDiscardTip);
            } finally { await h.Chat.TeardownAsync(); }
        });

    /// A message its sender discarded is over: the server's rejection of it tells nobody to send it again, and an own
    /// message it settles leaves with no notice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_discarded_message_leaves_with_no_notice() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();
            h.Chat.ComposerText = "mine";
            await h.Chat.SendCommand.Execute();
            var id = Guid.NewGuid();
            h.Queue.OnNext([Item("mine", id, state: "held")]);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);

            h.Queue.OnNext([Item("mine", id, state: "rejected", reason: "discarded"), Item("theirs", Guid.NewGuid(), sender: "u3", state: "rejected", reason: "discarded")]);

            await Assert.That(h.Chat.QueuedMessages).IsEmpty();
            await Assert.That(h.Chat.DeliveryNotices).IsEmpty();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// Disconnecting steering ends nothing in the terminal, so where the chat would say the session ended it says the
    /// steering did. A hosted session's end reads as before.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public Task An_attached_session_that_ends_reads_as_steering_ended(bool attached) => RunOnUiAsync(async () => {
        var h = new Rig(attached);
        try {
            await h.SeededAsync();

            h.Session.OnNext(h.Session.Value with { Ended = true });

            await WaitUntilAsync(() => !h.Chat.ShowsComposer, what: "the composer to close");
            if (attached) {
                await Assert.That(h.Chat.StatusText).IsEqualTo("Steering ended");
                await Assert.That(h.Chat.AgentStatus.Tip).IsEqualTo("Steering ended");
                await Assert.That(h.Chat.ComposerHint).IsEqualTo("Steering ended");
            } else {
                await Assert.That(h.Chat.StatusText).IsNotEqualTo("Steering ended");
                await Assert.That(h.Chat.ComposerHint).IsEqualTo(ServerChatInput.EndedHint);
            }
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// The server refuses an attached agent's Escape, so the button stays in place but disabled, pointing at the terminal.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_attached_sessions_interrupt_is_disabled_and_points_at_its_terminal() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();

            await Assert.That(h.Chat.ShowsInterrupt).IsTrue();
            await Assert.That(h.Chat.InterruptTip).IsEqualTo("Interrupt it in its terminal");
            await Assert.That(await h.Chat.InterruptCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(h.Chat.CanInterruptWithEscape).IsFalse();
        } finally { await h.Chat.TeardownAsync(); }
    });

    /// The server refuses files for an attached agent, so the composer says why before anything is staged.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_attached_session_says_files_cannot_be_sent_to_it() => RunOnUiAsync(async () => {
        var h = new Rig(attached: true);
        try {
            await h.SeededAsync();

            await Assert.That(h.Chat.Attachments.CanAttach).IsFalse();
            await Assert.That(h.Chat.Attachments.AttachHint).IsEqualTo("files can't be sent to a terminal session");
        } finally { await h.Chat.TeardownAsync(); }
    });

    static QueuedInputItem Item(string text, Guid id, string? sender = "u2", string? state = null, string? reason = null) =>
        new() { DispatchId = id, Text = text, SenderUserId = sender, DeliveryState = state, DeliveryReason = reason };

    /// A remote chat on a running session, its sends going through the server as the viewer u2.
    sealed class Rig {
        public readonly Subject<IReadOnlyList<QueuedInputItem>> Queue = new();
        public readonly BehaviorSubject<ChatSessionInfo> Session;
        public readonly ChatTabViewModel Chat;

        public Rig(bool attached = false, IObservable<ViewerIdentityDto?>? identity = null) {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "running", DaemonName = "work-mac", OwnerUserId = "u2", Vendor = "claude",
                Attached = attached,
            });
            var session = Session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: !attached,
                senderIdentity: identity ?? Observable.Return<ViewerIdentityDto?>(new() { UserId = "u2" }));
            var time = new FakeTimeProvider();
            Chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(),
                _ => new SeedFeed(), new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: Queue);
        }

        public Task SeededAsync() => WaitUntilAsync(() => Chat.HasSeed, what: "the seed");
    }

    /// Resets once, which seeds the chat, and reads nothing after.
    sealed class SeedFeed : IChatTranscriptFeed {
        bool _seeded;
        public long? CurrentOffset => 0;
        public FeedRead ReadAppended() {
            if (_seeded) return new(FeedStatus.Ok, []);
            _seeded = true;
            return new(FeedStatus.Reset, []);
        }
        public void Dispose() { }
    }
}
