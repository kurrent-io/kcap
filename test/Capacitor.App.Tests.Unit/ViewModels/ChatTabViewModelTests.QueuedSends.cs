using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// Enter on a recalled copy of a queued prompt leaves that one row. Send on the row, once the
    /// agent is idle, is what delivers it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_on_queued_text_does_not_add_a_second_row_and_send_delivers_it_once_idle() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            await Assert.That(queued.CanSendNow).IsTrue();
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.IdlePickupNote);

            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Pending).IsNull();
            await Assert.That(input.Sends).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(queued);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");

            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await Assert.That(queued.CanSendNow).IsFalse();
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.AwaitingPickupNote);
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Pending).IsNull();
            await Assert.That(input.Sends).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);

            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(input.Sends).Count().IsEqualTo(2);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);
            await Assert.That(h.Chat.QueuedMessages.Single().Text).IsEqualTo("look");
            await h.TeardownAsync();
        });
    }

    /// An idle row Send keeps an unrelated draft and leaves staged chips alone, including text
    /// typed while the channel is still deciding. The replacement is text-only, and a rejected
    /// attempt puts the original row back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_keeps_draft_and_tray_and_restores_the_row_on_refusal() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            h.Chat.ComposerText = "unrelated draft";
            h.Chat.Tray.AddAll([new StagedAttachment("note.txt", "text/plain", "hi"u8.ToArray())]);

            var refused = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await refused;
            await Assert.That(input.Sends.Last().Ids).IsEmpty();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unrelated draft");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(queued);
            await Assert.That(queued.CanSendNow).IsTrue();

            var accepted = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the queued send reaching the channel");
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unrelated draft");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            h.Chat.ComposerText = "typed while sending";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await accepted;
            await Assert.That(input.Sends.Last().Ids).IsEmpty();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typed while sending");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);
            await h.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false, false, ChatSendOutcome.Accepted, false)]
    [Arguments(false, false, ChatSendOutcome.Unconfirmed, false)]
    [Arguments(false, false, ChatSendOutcome.Unconfirmed, true)]
    [Arguments(true, false, ChatSendOutcome.Accepted, false)]
    [Arguments(true, false, ChatSendOutcome.Unconfirmed, false)]
    [Arguments(true, false, ChatSendOutcome.Unconfirmed, true)]
    [Arguments(true, true, ChatSendOutcome.Accepted, false)]
    [Arguments(true, true, ChatSendOutcome.Unconfirmed, false)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_clears_only_an_unchanged_recall(bool recall, bool editBack, ChatSendOutcome outcome, bool echoFirst) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var queued = h.Chat.QueuedMessages.Single();
                if (recall) await Assert.That(h.Chat.RecallOlder()).IsTrue();
                else h.Chat.ComposerText = "look";
                if (editBack) {
                    h.Chat.ComposerText = "look again";
                    h.Chat.ComposerText = "look";
                }

                var send = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                if (echoFirst) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(outcome);
                await send;
                var ownsDraft = recall && !editBack;
                await Assert.That(h.Chat.ComposerText).IsEqualTo(ownsDraft && (echoFirst || outcome == ChatSendOutcome.Accepted) ? "" : "look");
                if (!echoFirst) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(ownsDraft ? "" : "look");
                await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(ownsDraft ? "" : "look");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Unconfirmed)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retried_twice_is_retired_by_the_next_echo(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.QueuedMessages.Single();
                await SendAsync(h, input, "look");
                var other = h.Chat.QueuedMessages[^1];
                for (var attempt = 0; attempt < 2; attempt++) {
                    await Assert.That(retry.CanSendNow).IsTrue();
                    var send = h.Chat.SendQueuedCommand.Execute(retry).ToTask();
                    input.Pending!.SetResult(outcome);
                    await send;
                    retry = h.Chat.QueuedMessages[^1];
                }

                await Assert.That(input.Sends).Count().IsEqualTo(4);
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(other);
                await SendAsync(h, input, "look");
                for (var echo = 0; echo < 3; echo++) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                    await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(other);
                }
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted, false)]
    [Arguments(ChatSendOutcome.Accepted, true)]
    [Arguments(ChatSendOutcome.Unconfirmed, false)]
    [Arguments(ChatSendOutcome.Unconfirmed, true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retry_echoes_preserve_a_send_between_retries(ChatSendOutcome outcome, bool echoBeforeRetry) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                var firstRetry = h.Chat.QueuedMessages.Single();

                h.Chat.ComposerText = "look";
                var independentSend = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await independentSend;
                var independent = h.Chat.QueuedMessages[^1];
                if (echoBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                retry = h.Chat.SendQueuedCommand.Execute(firstRetry).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;

                await Assert.That(input.Sends).Count().IsEqualTo(4);
                if (echoBeforeRetry) {
                    await h.TickAsync();
                    await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
                }
                for (var echo = 0; echo < (echoBeforeRetry ? 2 : 3); echo++) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                    var expectedDraft = outcome == ChatSendOutcome.Unconfirmed ? "look" : "";
                    await Assert.That(h.Chat.ComposerText).IsEqualTo(expectedDraft);
                    await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(expectedDraft);
                    await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                    await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(independent);
                }
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Unconfirmed)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_after_reset_of_an_acknowledged_retry_is_retired_by_its_echo(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine, AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();

                File.WriteAllLines(path, [AssistantLine]);
                await h.TickAsync();
                h.Chat.ComposerText = "look";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await send;
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_second_retry_refusal_preserves_echo_ownership(bool echoDuringRetry) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                var firstRetry = h.Chat.QueuedMessages.Single();

                h.Chat.ComposerText = "look";
                var independentSend = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await independentSend;
                var independent = h.Chat.QueuedMessages[^1];
                retry = h.Chat.SendQueuedCommand.Execute(firstRetry).ToTask();
                if (echoDuringRetry) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(ChatSendOutcome.Rejected);
                await retry;
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(echoDuringRetry ? 1 : 2);
                if (!echoDuringRetry) await Assert.That(h.Chat.QueuedMessages[^1]).IsSameReferenceAs(firstRetry);
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");

                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(independent);
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Rejected)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retries_unconfirmed_text_only_when_idle(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "look";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                var queued = h.Chat.QueuedMessages.Single();
                await Assert.That(queued.IsUnconfirmed).IsTrue();
                await Assert.That(queued.CanSendNow).IsTrue();
                await h.PushAsync(Dto(path) with { Status = "Running", AwaitingInput = false });
                await Assert.That(queued.CanSendNow).IsFalse();
                await h.Chat.SendQueuedCommand.Execute(queued);
                await Assert.That(input.Sends).Count().IsEqualTo(1);

                await h.PushAsync(Dto(path) with { Status = "Running", AwaitingInput = true });
                var retry = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                input.Pending!.SetResult(outcome);
                await retry;
                var remaining = h.Chat.QueuedMessages.Single();
                if (outcome == ChatSendOutcome.Rejected) await Assert.That(remaining).IsSameReferenceAs(queued);
                else await Assert.That(remaining).IsNotSameReferenceAs(queued);
                await Assert.That(remaining.IsUnconfirmed).IsEqualTo(outcome == ChatSendOutcome.Rejected);
                await Assert.That(remaining.CanSendNow).IsTrue();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(outcome == ChatSendOutcome.Accepted ? "" : "look");

                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_refusal_does_not_restore_an_echoed_prompt(bool echoedBeforeRetry) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var queued = h.Chat.QueuedMessages.Single();
                h.Chat.ComposerText = "look";
                if (echoedBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                var retry = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                if (!echoedBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                input.Pending!.SetResult(ChatSendOutcome.Rejected);
                await retry;

                await Assert.That(input.Sends).Count().IsEqualTo(2);
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Two same-text rows plus an unedited recall must not block an explicit Send on one of them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_bypasses_recall_dedup_when_another_same_text_row_remains() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            await SendAsync(h, input, "look");
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
            var first = h.Chat.QueuedMessages[0];
            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("look");

            var forced = h.Chat.SendQueuedCommand.Execute(first).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(input.Sends).Count().IsEqualTo(3);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
            await Assert.That(h.Chat.QueuedMessages.Any(q => ReferenceEquals(q, first))).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    /// The resend's own echo retires the replacement. A later copy of that text is the dropped
    /// row's, so the banner stays clear.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_resend_echo_retires_the_replacement_and_a_later_copy_is_absorbed() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);

            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Once the resend has echoed, a later copy of its text is absorbed. Another row that happens
    /// to carry the same text stays queued.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_echo_of_a_resent_row_leaves_another_same_text_row_queued() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            await SendAsync(h, input, "look");
            var first = h.Chat.QueuedMessages[0];
            var second = h.Chat.QueuedMessages[1];
            var forced = h.Chat.SendQueuedCommand.Execute(first).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;

            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(second);
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(second);
            await h.TeardownAsync();
        });
    }

    /// A send made after the resend has echoed owns that echo. The dropped row does not take it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_after_a_resent_row_echoed_is_retired_by_its_own_echo() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();

            h.Chat.ComposerText = "look";
            var again = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await again;
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Teardown disposes the busy signal while the queued send is still awaiting the channel.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_during_a_queued_send_preserves_independent_drafts(bool recall) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            if (recall) await Assert.That(h.Chat.RecallOlder()).IsTrue();
            else h.Chat.ComposerText = "look";
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the queued send reaching the channel");
            await h.TeardownAsync();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(recall ? "" : "look");
        });
    }

    /// A send still uploading keeps CanAcceptText true; the busy gate hides row Send until it ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_is_hidden_while_another_send_is_uploading() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            await Assert.That(queued.CanSendNow).IsTrue();

            h.Chat.ComposerText = "with file";
            h.Chat.Tray.AddAll([new StagedAttachment("shot.png", "image/png", new byte[] { 1 })]);
            var send = h.Chat.SendCommand.Execute().ToTask();
            await uploader.Started.Task;
            await Assert.That(queued.CanSendNow).IsFalse();
            await h.Chat.SendQueuedCommand.Execute(queued);
            await Assert.That(input.Sends).Count().IsEqualTo(1);

            uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
            await WaitUntilAsync(() => input.Pending is not null, what: "the send reaching the channel");
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(queued.CanSendNow).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// Text equality cannot prove a recall was left alone: an edit undone by hand lands on the
    /// same string, and that string is the user's own draft now.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_ends_when_a_recalled_prompt_is_edited_and_restored() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            foreach (var text in new[] { "first", "second" }) {
                h.Chat.ComposerText = text;
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
            }
            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");

            h.Chat.ComposerText = "second!";
            h.Chat.ComposerText = "second";
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_messages_ignore_old_echoes_and_acknowledge_repeated_text_one_at_a_time() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("queued.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            // Unread history with identical text is older than both sends.
            File.AppendAllText(path, UserLine + "\n");
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            h.Chat.ComposerText = "hello";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await h.TickAsync();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("2 messages queued");
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The channel holds an attachment send while the daemon fetches the files: until the channel
    /// takes it, the prompt is in the composer and nowhere else.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_attachment_send_waiting_on_the_channel_shows_only_in_the_composer() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            try {
                var path = Tmp.CreateFile("pasted.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "look at this";
                h.Chat.Tray.AddAll([new StagedAttachment("shot.png", "image/png", new byte[] { 1 })]);
                var send = h.Chat.SendCommand.Execute().ToTask();
                await uploader.Started.Task;
                uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
                await WaitUntilAsync(() => input.Pending is not null, what: "the send reaching the channel");
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look at this");

                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A prompt Claude Code takes up mid-turn is recorded only as a queued_command attachment:
    /// that record is its echo, and the chat shows it as the user's turn.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_prompt_taken_up_mid_turn_leaves_the_queue_and_shows_as_a_user_turn() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("absorbed.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "also check the toggle";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");

                File.AppendAllText(path, """{"type":"attachment","attachment":{"type":"queued_command","prompt":"also check the toggle","commandMode":"prompt","origin":{"kind":"human"}}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Single().Text).IsEqualTo("also check the toggle");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_echo_before_the_send_ack_is_not_requeued_and_a_refusal_keeps_the_draft() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("queued.jsonl", []);
            await h.PushAsync(Dto(path));
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            h.Chat.ComposerText = "refused";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("refused");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_keeps_unconfirmed_input_until_a_fresh_echo() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("before.jsonl", [UserLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                var other = Tmp.CreateFile("after.jsonl", [UserLine]);
                await h.PushAsync(Dto(other));
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                File.AppendAllText(other, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_rebases_input_without_acknowledging_replayed_history() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("reset.jsonl", [UserLine, AssistantLine, AssistantLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.WriteAllLines(path, [UserLine]);
                await h.TickAsync();
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                File.AppendAllText(path, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_lost_ack_keeps_input_unconfirmed_until_a_transcript_echo(bool newerSend) {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            using var presence = new System.Reactive.Subjects.BehaviorSubject<AgentPresence>(
                new AgentPresence(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" }, false));
            var ops = new ScriptedLocalControlOps();
            var input = new LocalFrameChatInput("a1", daemon, ops, presence);
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1"]));
                var path = Tmp.CreateFile("lost-ack.jsonl", []);
                await h.PushAsync(Hosted(path, "Running", false));
                ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, "lost ack", null));
                h.Chat.ComposerText = "hello";
                await h.Chat.SendCommand.Execute();
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                await Assert.That(h.Chat.ComposerText).IsEqualTo("hello");
                if (newerSend) {
                    ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, "another lost ack", null));
                    h.Chat.ComposerText = "later";
                    await h.Chat.SendCommand.Execute();
                }
                File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hello")) + "\n");
                await h.TickAsync();
                if (newerSend) {
                    await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                    await Assert.That(h.Chat.ComposerHint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
                    await Assert.That(h.Chat.ComposerText).IsEqualTo("later");
                    File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "later")) + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await Assert.That(h.Chat.ComposerHint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("!kubectl get pods")]
    [Arguments("! kubectl get pods")]
    public async Task A_bang_command_echo_clears_the_queue_and_shows_the_command_and_output(string typed) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("bash.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = typed;
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}""" + "\n");
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-stdout>web</bash-stdout><bash-stderr></bash-stderr>"}}""" + "\n");
                File.AppendAllText(path, NoteLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                var shell = h.Chat.Items.OfType<ShellCommandItem>().Single();
                await Assert.That(shell.Command).IsEqualTo("! kubectl get pods");
                await Assert.That(shell.Output).IsEqualTo("web");
                await Assert.That(h.Chat.Items.OfType<UserTurnItem>()).IsEmpty();
                await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Single().Text).IsEqualTo("**Agent finished**\n\nAll good.");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Bang_output_fills_the_command_row_when_it_arrives_later() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                var path = Tmp.CreateFile("bash-later.jsonl", ["""{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}"""]);
                await h.PushAsync(Dto(path));
                var shell = h.Chat.Items.OfType<ShellCommandItem>().Single();
                await Assert.That(shell.HasOutput).IsFalse();
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-stdout>web</bash-stdout>"}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.Items.OfType<ShellCommandItem>().Single()).IsSameReferenceAs(shell);
                await Assert.That(shell.Output).IsEqualTo("web");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_command_result_settles_one_send_without_a_chat_row() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                const string result = """{"type":"system","subtype":"local_command","commandRun":{"command":"rename","args":"receipt check"},"content":"<local-command-stdout>Session renamed to: receipt check</local-command-stdout>"}""";
                var path = Tmp.CreateFile("local-command.jsonl", [result]);
                await h.PushAsync(Dto(path));
                for (var i = 0; i < 2; i++) {
                    h.Chat.ComposerText = "/rename receipt check";
                    var send = h.Chat.SendCommand.Execute().ToTask();
                    input.Pending!.SetResult(ChatSendOutcome.Accepted);
                    await send;
                }
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
                File.AppendAllText(path, """{"type":"system","subtype":"local_command","content":"<command-name>/rename</command-name><command-args>receipt check</command-args>"}""" + "\n");
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                await Assert.That(h.Chat.Items).IsEmpty();
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items).IsEmpty();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_slash_command_echo_acknowledges_input_without_a_display_row() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("slash.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "/clear";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.AppendAllText(path, """{"type":"user","message":{"content":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items).IsEmpty();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Unconfirmed_send_and_echo_can_arrive_in_either_order_without_erasing_a_new_draft(bool echoFirst, bool editBack) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("uncertain.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                h.Chat.ComposerText = "another draft";
                if (editBack) h.Chat.ComposerText = "hello";
                if (echoFirst) {
                    File.AppendAllText(path, UserLine + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                if (!echoFirst) {
                    await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsTrue();
                    File.AppendAllText(path, UserLine + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(editBack ? "hello" : "another draft");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(ChatSendOutcome.Accepted, false)]
    [Arguments(ChatSendOutcome.Rejected, false)]
    [Arguments(ChatSendOutcome.Unconfirmed, true)]
    public async Task Without_a_projection_only_uncertain_delivery_stays_in_the_queue(ChatSendOutcome outcome, bool queued) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(null, input: input);
            try {
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await send;
                await Assert.That(h.Chat.HasQueuedMessages).IsEqualTo(queued);
                await Assert.That(h.Chat.ComposerText).IsEqualTo(outcome == ChatSendOutcome.Accepted ? "" : "hello");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Initial_history_and_preexisting_partial_lines_cannot_acknowledge_a_send(bool missing) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = missing ? Tmp.PathTo("missing.jsonl") : Tmp.CreateFile("partial.jsonl", UserLine);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                if (missing) File.WriteAllText(path, UserLine + "\n");
                else File.AppendAllText(path, "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                File.AppendAllText(path, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Mixed_queue_distinguishes_unconfirmed_and_accepted_sends_and_marks_both_at_session_end() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                h.Chat.ComposerText = "next";
                send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued · 1 unconfirmed");
                await Assert.That(h.Chat.QueuedMessages[0].IsUnconfirmed).IsTrue();
                await Assert.That(h.Chat.QueuedMessages[1].IsUnconfirmed).IsFalse();
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Completed" });
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("2 messages unconfirmed");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A cache removal ends the session. The visible word is Done — the daemon has already
    /// dropped the agent — and a send that can never be echoed stops claiming it is queued.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_removed_agent_reads_as_done_and_unconfirms_the_queue() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.StatusText).IsEqualTo("Running");
                await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsFalse();

                h.Daemon.Agents.Remove("a1");
                await Assert.That(h.Chat.StatusText).IsEqualTo("Done");
                await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }
}
