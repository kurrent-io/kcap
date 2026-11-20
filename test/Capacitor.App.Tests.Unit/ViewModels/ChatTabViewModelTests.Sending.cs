using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_clears_only_when_committed_and_the_text_is_unchanged() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Sends.Single().Text).IsEqualTo("hello");
            h.Chat.ComposerText = "hello edited";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hello edited");

            h.Chat.ComposerText = "two";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("two");

            h.Chat.ComposerText = "three";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    /// The session is captured as each prompt goes out, and only a prompt the channel accepted can
    /// have answered its open questions, whether or not its tab outlived the send.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_an_accepted_send_reports_its_session_as_answered() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { HonoursCancellation = false };
            var time = new FakeTimeProvider();
            var sending = new List<string>();
            var answered = new List<string>();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, new BehaviorSubject<ChatSessionInfo>(Session("s1")), Observable.Return<string[]?>(null), input,
                new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true }, new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time),
                sessionId: new BehaviorSubject<string?>("s1")) {
                InputSending = sid => { sending.Add(sid); return () => answered.Add(sid); },
            };
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "refused";
            var send = chat.SendCommand.Execute().ToTask();
            await Assert.That(sending).IsEquivalentTo(["s1"]);
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(answered).IsEmpty();

            chat.ComposerText = "delivered";
            send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(answered).IsEquivalentTo(["s1"]);

            chat.ComposerText = "delivered after the tab closed";
            send = chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            await chat.TeardownAsync();
            pending.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(answered).IsEquivalentTo(["s1", "s1"]);
        });
    }

    /// While the local daemon reports another server, its session ids name that server's sessions,
    /// so a reply there answers nothing the app's tracker holds.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_send_reports_its_session_only_while_the_daemon_is_on_the_app_server() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var time = new FakeTimeProvider();
            var onAppServer = new BehaviorSubject<bool>(false);
            var sending = new List<string>();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Local, new BehaviorSubject<ChatSessionInfo>(Session("s1")), Observable.Return<string[]?>(null), input,
                new NoAttachmentUploader(), _ => new EmptyFeed(), new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time),
                sessionId: new BehaviorSubject<string?>("s1"), localDaemonOnAppServer: onAppServer) {
                InputSending = sid => { sending.Add(sid); return () => { }; },
            };

            chat.ComposerText = "elsewhere";
            var send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(sending).IsEmpty();

            onAppServer.OnNext(true);
            chat.ComposerText = "here";
            send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(sending).IsEquivalentTo(["s1"]);
            await chat.TeardownAsync();
        });
    }

    /// Pins that a draft outlives its tab, which navigation tears down, and that a sent one does not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unsent_draft_survives_the_tab_being_rebuilt() {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "half a thought";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            await Assert.That(second.Chat.ComposerText).IsEqualTo("half a thought");
            var send = second.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await second.TeardownAsync();

            var third = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            await Assert.That(third.Chat.ComposerText).IsEqualTo("");
            await third.TeardownAsync();
        });
    }

    /// Pins a send that settles after its tab is gone: only a confirmed delivery clears the draft —
    /// in the store and in the tab now showing it — and never over text typed since, in the old tab
    /// or the one that replaced it.
    [Test]
    [Arguments(ChatSendOutcome.Accepted, false, false, "")]
    [Arguments(ChatSendOutcome.Rejected, false, false, "hello")]
    [Arguments(ChatSendOutcome.Unconfirmed, false, false, "hello")]
    [Arguments(ChatSendOutcome.Accepted, true, false, "hello again")]
    [Arguments(ChatSendOutcome.Accepted, false, true, "hello there")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_settling_after_teardown_clears_only_its_own_delivered_draft(
            ChatSendOutcome outcome, bool editedBeforeLeaving, bool editedInReplacement, string expected) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "hello";
            var send = first.Chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            if (editedBeforeLeaving) first.Chat.ComposerText = "hello again";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            if (editedInReplacement) second.Chat.ComposerText = "hello there";
            pending.SetResult(outcome);
            await send;
            await Assert.That(second.Chat.ComposerText).IsEqualTo(expected);
            await second.TeardownAsync();

            var third = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            await Assert.That(third.Chat.ComposerText).IsEqualTo(expected);
            await third.TeardownAsync();
        });
    }

    /// Pins that only a send the channel did not refuse counts as a submitted turn.
    [Test]
    [Arguments(ChatSendOutcome.Accepted, 1)]
    [Arguments(ChatSendOutcome.Unconfirmed, 1)]
    [Arguments(ChatSendOutcome.Rejected, 0)]
    [NotInParallel("AvaloniaSession")]
    public async Task TurnSubmitted_fires_only_for_a_send_the_channel_did_not_refuse(ChatSendOutcome outcome, int expected) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            var submitted = 0;
            h.Chat.TurnSubmitted += () => submitted++;

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(submitted).IsEqualTo(0);
            input.Pending!.SetResult(outcome);
            await send;
            await Assert.That(submitted).IsEqualTo(expected);
            await h.TeardownAsync();
        });
    }

    /// A send that went out is recallable whether or not the transcript has echoed it yet; a
    /// rejected one never left the box, so there is nothing to recall.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_walks_the_sent_prompts_and_skips_a_rejected_send() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "first";
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            h.Chat.ComposerText = "refused";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            h.Chat.ComposerText = "second";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            h.Chat.ComposerText = "";

            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("first");
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Sending a recall over a typed draft hands the draft back to the box and the stored draft,
    /// the only places the user can reach it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sending_a_recall_restores_the_draft_it_replaced() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var drafts = new ComposerDrafts();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));

            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("first");
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;

            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo("typing");
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Sending the stash itself, typed back over a recall, clears the box like any send.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sending_text_equal_to_the_stash_clears_the_box() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var drafts = new ComposerDrafts();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));

            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            h.Chat.ComposerText = "typing";
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;

            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    /// The history dies with the tab: a recall on screen hands the stored draft back to the typed
    /// draft, and an edited recall leaves the typed draft stashed for the next tab.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Tearing_down_the_tab_keeps_the_stashed_draft(bool edited) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var first = new Harness(TranscriptChat.For("claude"), input: new ScriptedInput(), drafts: drafts);
            await first.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            first.Chat.ComposerText = "typing";
            await Assert.That(first.Chat.RecallOlder()).IsTrue();
            if (edited) first.Chat.ComposerText = "first!";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            if (edited) {
                await Assert.That(second.Chat.ComposerText).IsEqualTo("first!");
                second.Chat.ComposerText = "";
                await Assert.That(second.Chat.RecallNewer()).IsTrue();
            }
            await Assert.That(second.Chat.ComposerText).IsEqualTo("typing");
            await second.TeardownAsync();
        });
    }

    /// Recall waits for the box's own send to settle: an accepted send leaves an empty box to
    /// recall from, a refused one leaves its text as a draft that recall stashes.
    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Rejected)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_in_flight_blocks_recall_until_it_settles(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            h.Chat.ComposerText = "typing";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");

            input.Pending!.SetResult(outcome);
            await send;
            if (outcome == ChatSendOutcome.Accepted) {
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await Assert.That(h.Chat.RecallOlder()).IsTrue();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            } else {
                await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
                await Assert.That(h.Chat.RecallOlder()).IsTrue();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("first");
                await Assert.That(h.Chat.RecallNewer()).IsTrue();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            }
            await h.TeardownAsync();
        });
    }

    /// Closing the tab with the send still out leaves the stored copy to that send's outcome:
    /// delivery clears it, a refusal keeps it.
    [Test]
    [Arguments(ChatSendOutcome.Accepted, "")]
    [Arguments(ChatSendOutcome.Rejected, "typing")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_settling_after_teardown_decides_its_own_text(ChatSendOutcome outcome, string expected) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "typing";
            var send = first.Chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            await Assert.That(first.Chat.RecallOlder()).IsFalse();
            await Assert.That(first.Chat.RecallOlder()).IsFalse();
            await first.TeardownAsync();

            pending.SetResult(outcome);
            await send;
            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            await Assert.That(second.Chat.ComposerText).IsEqualTo(expected);
            await Assert.That(second.Chat.RecallNewer()).IsFalse();
            await second.TeardownAsync();
        });
    }

    /// Typing while the send is out makes the box a draft of the user's own, even when it ends
    /// up as the same words: recall stashes it, and the send's delivery cannot take it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retyped_copy_of_the_prompt_in_flight_is_a_draft_recall_keeps() {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "typing";
            var send = first.Chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            first.Chat.ComposerText = "typing!";
            first.Chat.ComposerText = "typing";
            await Assert.That(first.Chat.RecallOlder()).IsTrue();
            await first.TeardownAsync();

            pending.SetResult(ChatSendOutcome.Accepted);
            await send;
            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            await Assert.That(second.Chat.ComposerText).IsEqualTo("typing");
            await second.TeardownAsync();
        });
    }

    /// An edit made while the send is out is stashed by recall and comes back on ↓ whatever the
    /// send's outcome, and nothing of the send is left stashed behind it.
    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Rejected)]
    [NotInParallel("AvaloniaSession")]
    public async Task An_edit_made_while_sending_survives_the_send_settling(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var first = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            await first.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            first.Chat.ComposerText = "typing";
            var send = first.Chat.SendCommand.Execute().ToTask();
            first.Chat.ComposerText = "typing!";
            await Assert.That(first.Chat.RecallOlder()).IsTrue();
            input.Pending!.SetResult(outcome);
            await send;

            var presses = 0;
            while (first.Chat.ComposerText != "typing!" && presses++ < 5) first.Chat.RecallNewer();
            await Assert.That(first.Chat.ComposerText).IsEqualTo("typing!");
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            await Assert.That(second.Chat.ComposerText).IsEqualTo("typing!");
            second.Chat.ComposerText = "";
            await Assert.That(second.Chat.RecallNewer()).IsFalse();
            await second.TeardownAsync();
        });
    }

    /// Another transcript under an edited recall leaves the edit in the box and the typed draft
    /// stashed behind it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_transcript_switch_over_an_edited_recall_keeps_the_stash() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), input: new ScriptedInput());
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            h.Chat.ComposerText = "first!";

            await h.PushAsync(Dto(Tmp.CreateFile("o.jsonl", [User("other")])));
            await Assert.That(h.Chat.ComposerText).IsEqualTo("first!");
            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            await h.TeardownAsync();
        });
    }

    /// A stash a reopened tab took from the store survives that tab's first transcript.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reopened_tab_keeps_its_stored_stash_through_a_feed_update() {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var first = new Harness(TranscriptChat.For("claude"), input: new ScriptedInput(), drafts: drafts);
            await first.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            first.Chat.ComposerText = "typing";
            await Assert.That(first.Chat.RecallOlder()).IsTrue();
            first.Chat.ComposerText = "first!";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.For("claude"), input: new ScriptedInput(), drafts: drafts);
            await second.PushAsync(Dto(Tmp.CreateFile("o.jsonl", [User("other")])));
            await Assert.That(second.Chat.ComposerText).IsEqualTo("first!");
            second.Chat.ComposerText = "";
            await Assert.That(second.Chat.RecallNewer()).IsTrue();
            await Assert.That(second.Chat.ComposerText).IsEqualTo("typing");
            await second.TeardownAsync();
        });
    }

    /// The typed draft written back at teardown is newer than the recall a still-pending send
    /// carried, so that send's delivery cannot clear it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_recall_send_settling_after_teardown_leaves_the_restored_draft() {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var first = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            await first.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("first")])));
            first.Chat.ComposerText = "typing";
            await Assert.That(first.Chat.RecallOlder()).IsTrue();
            var send = first.Chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            await first.TeardownAsync();

            pending.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo("typing");
        });
    }

    /// Re-sending a recall still waiting for its echo sends nothing, and the box still gets the
    /// typed draft back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Resending_a_queued_recall_restores_the_draft_it_replaced() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            await SendAsync(h, input, "first");

            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await h.Chat.SendCommand.Execute().ToTask();

            await Assert.That(input.Pending).IsNull();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            await h.TeardownAsync();
        });
    }

    /// An edited recall over a typed draft is a second draft: neither recall direction overwrites
    /// it or the stash, and clearing the box lets ↓ bring the typed draft back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_edited_recall_never_costs_the_typed_draft() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            await SendAsync(h, input, "second");

            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            h.Chat.ComposerText = "second!";
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second!");

            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            await h.TeardownAsync();
        });
    }

    static async Task TickForAsync(Harness h, TimeSpan span) {
        for (var waited = TimeSpan.Zero; waited <= span; waited += ChatTabViewModel.PollInterval) await h.TickAsync();
    }

    /// An idle agent that never echoes a send may have taken its Enter as a newline, as it does for
    /// a large paste still arriving: Enter is pressed once more, then the row says it was not picked
    /// up instead of waiting for a confirmation that will not come.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_unechoed_send_to_an_idle_agent_is_resubmitted_once_then_reads_not_picked_up(bool resubmitted) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true, ResubmitResult = resubmitted };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(input.Resubmits).IsEqualTo(1);
            await Assert.That(queued.IsNotPickedUp).IsFalse();

            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(queued.IsNotPickedUp).IsTrue();
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.NotPickedUpNote);
            await Assert.That(queued.CanSendNow).IsTrue();

            await TickForAsync(h, ChatTabViewModel.PickupWait * 3);
            await Assert.That(input.Resubmits).IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    /// A send made mid-turn waits in the agent's own queue until the turn ends, so the clock runs
    /// only while the agent is not working.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_queued_behind_a_turn_is_not_resubmitted_while_the_agent_works() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false, TurnAttested = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await TickForAsync(h, ChatTabViewModel.PickupWait * 4);
            await Assert.That(input.Resubmits).IsEqualTo(0);
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.AwaitingPickupNote);

            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(input.Resubmits).IsEqualTo(1);
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(queued.IsNotPickedUp).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// A turn started after a send from an idle prompt means its Enter landed, even when the echo
    /// does not match the sent text. A second Enter would submit whatever was typed since.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_from_idle_followed_by_a_turn_is_never_resubmitted_or_marked_not_picked_up() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await h.PushAsync(Dto(path) with { AwaitingInput = false, TurnAttested = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait * 4);

            await Assert.That(input.Resubmits).IsEqualTo(0);
            await Assert.That(queued.IsNotPickedUp).IsFalse();
            await Assert.That(h.Chat.QueuedMessages).Contains(queued);
            await h.TeardownAsync();
        });
    }

    /// A subagent still running when the agent's own prompt waits does not make a send there a
    /// mid-turn one: the agent's next attested turn is the send's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_while_only_a_subagent_works_is_left_waiting_once_a_turn_starts() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true, LiveSubagents = 1 });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await h.PushAsync(Dto(path) with { AwaitingInput = false, TurnAttested = true, LiveSubagents = 1 });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await h.PushAsync(Dto(path) with { AwaitingInput = true, LiveSubagents = 0 });
            await TickForAsync(h, ChatTabViewModel.PickupWait * 4);

            await Assert.That(input.Resubmits).IsEqualTo(0);
            await Assert.That(queued.IsNotPickedUp).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The submit clears the agent's wait even when its Enter only became a newline, so working
    /// that no turn attests is not a turn: the row still gets its second Enter.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unattested_wait_cleared_by_the_submit_does_not_stop_the_resubmit() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(input.Resubmits).IsEqualTo(1);
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await Assert.That(queued.IsNotPickedUp).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// A send made mid-turn counts a turn only once the agent has gone idle after it: the turn that
    /// was already running is not its own.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_queued_behind_a_turn_is_left_waiting_once_a_later_turn_starts() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { CanResubmitValue = true };
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false, TurnAttested = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();

            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await h.PushAsync(Dto(path) with { AwaitingInput = false, TurnAttested = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await TickForAsync(h, ChatTabViewModel.PickupWait * 4);

            await Assert.That(input.Resubmits).IsEqualTo(0);
            await Assert.That(queued.IsNotPickedUp).IsFalse();
            await h.TeardownAsync();
        });
    }

    static async Task AssertRecallOrder(ChatTabViewModel chat, params string[] newestFirst) {
        chat.ComposerText = "";
        foreach (var expected in newestFirst) {
            await Assert.That(chat.RecallOlder()).IsTrue();
            await Assert.That(chat.ComposerText).IsEqualTo(expected);
        }
        await Assert.That(chat.RecallOlder()).IsFalse();
    }

    /// A workspace opened on an existing session recalls what its transcript records as
    /// submitted, slash commands included, and a rewritten transcript replaces it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_reaches_the_input_the_transcript_records() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string compact = """{"type":"user","message":{"content":"<command-name>/compact</command-name>"}}""";
            var path = Tmp.CreateFile("t.jsonl", [User("first"), AssistantLine, compact, UserLine]);
            await h.PushAsync(Dto(path));
            await AssertRecallOrder(h.Chat, "hello", "/compact", "first");

            File.WriteAllLines(path, [User("rewritten")]);
            await h.TickAsync();
            await AssertRecallOrder(h.Chat, "rewritten");
            await h.TeardownAsync();
        });
    }

    /// A send that lands before the read carrying older turns stays newest, and its echo moves it
    /// into the transcript's entries rather than adding a second copy.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_awaiting_its_echo_is_newest_and_its_echo_does_not_double_it() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            File.AppendAllText(path, User("old") + "\n");
            await SendAsync(h, input, "new");
            await h.TickAsync();
            await AssertRecallOrder(h.Chat, "new", "old");

            File.AppendAllText(path, User("new") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await AssertRecallOrder(h.Chat, "new", "old");
            await h.TeardownAsync();
        });
    }

    /// A replay can carry an older turn with the same text as a send still in flight; that turn is
    /// history, not the send's echo.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_replayed_turn_does_not_settle_a_newer_send_with_the_same_text() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [User("dropped"), User("a"), User("b")]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "a");

            File.WriteAllLines(path, [User("a"), User("b")]);
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            await AssertRecallOrder(h.Chat, "a", "b", "a");
            await h.TeardownAsync();
        });
    }

    /// Claude records a slash command as its name and trimmed arguments joined by one space.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_slash_command_sent_with_extra_spacing_is_acknowledged_by_its_receipt() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "/review   1504 ");
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();

            File.AppendAllText(path, """{"type":"user","message":{"content":"<command-name>/review</command-name><command-args>1504</command-args>"}}""" + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await AssertRecallOrder(h.Chat, "/review 1504");
            await h.TeardownAsync();
        });
    }

    /// Another transcript recalls nothing from the last, and a recall on screen gives way to the
    /// draft it replaced.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Another_transcript_recalls_nothing_sent_while_reading_the_last() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("before")])));
            await SendAsync(h, input, "unechoed");
            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unechoed");

            await h.PushAsync(Dto(Tmp.CreateFile("o.jsonl", [User("other")])));
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typing");
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await AssertRecallOrder(h.Chat, "other");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_leaves_out_the_attachment_trailer_the_daemon_appended() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string withFiles = """{"type":"user","message":{"role":"user","content":"look at this\n\n[Attached files: /x/a.png]"}}""";
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [withFiles])));
            await AssertRecallOrder(h.Chat, "look at this");
            await h.TeardownAsync();
        });
    }

    /// Text alone cannot decide this: an edit during the round trip that ends on the sent text is
    /// still the user's own draft, and clearing it would erase what they typed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_edited_back_to_the_sent_value_during_the_round_trip_survives() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            h.Chat.ComposerText = "hello!";
            h.Chat.ComposerText = "hello";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;

            await Assert.That(h.Chat.ComposerText).IsEqualTo("hello");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_an_in_flight_send_before_disposing_the_input_once() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            var ct = input.Sends.Single().Ct;
            await Assert.That(ct.IsCancellationRequested).IsFalse();

            await h.TeardownAsync();

            await Assert.That(ct.IsCancellationRequested).IsTrue();
            await Assert.That(input.Disposals).IsEqualTo(1);
            input.Pending?.TrySetCanceled(ct);
            try { await send; } catch (OperationCanceledException) { }
        });
    }

    sealed class EmptyFeed : IChatTranscriptFeed {
        public FeedRead ReadAppended() => new(FeedStatus.Ok, []);
        public long? CurrentOffset => 0;
        public void Dispose() { }
    }
}
