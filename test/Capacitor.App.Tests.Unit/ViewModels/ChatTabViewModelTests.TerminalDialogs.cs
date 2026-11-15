using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_usage_limit_question_is_shown_and_a_message_is_not_sent() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit · resets 3:10pm",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                    new(3, "Ask your admin for more usage"),
                ]);
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                h.Chat.ComposerText = "keep going";

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuChoices.Select(c => c.Label).ToArray()).IsEquivalentTo(new[] {
                    "Stop and wait for limit to reset",
                    "Wait here, then continue automatically shortly",
                    "Ask your admin for more usage",
                }, CollectionOrdering.Matching);
                await Assert.That(h.Chat.ComposerHint).Contains("usage limit");
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
                await h.Chat.TerminalMenuChoices[2].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'3' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.HasTerminalMenu).IsFalse();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_usage_limit_choice_sends_one_digit_and_a_failed_send_can_be_retried() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                ]);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Gate = release.Task;
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });

                var running = h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();
                release.SetResult();
                await running;
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1' }, CollectionOrdering.Matching);

                input.Accept = false;
                input.Gate = null;
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with {
                    Status = "Running",
                    UsageLimit = new UsageLimitNoticeDto(notice.Kind, "limit still held", notice.Prompt, notice.Options),
                });
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(h.Chat.TerminalMenuError).Contains("not attached");
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsTrue();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                input.Accept = true;
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1', (byte)'2' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_still_uploading_when_the_usage_limit_appears_is_not_pasted_into_the_menu() {
        await RunOnUiAsync(async () => {
            var input = new RecordingChatInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                ]);
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                h.Chat.ComposerText = "keep going";
                h.Chat.Tray.AddAll([new StagedAttachment("note.txt", "text/plain", "hi"u8.ToArray())]);

                var sending = h.Chat.SendCommand.Execute().ToTask();
                await uploader.Started.Task;
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
                await sending;

                await Assert.That(input.Sent).IsEmpty();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("keep going");
                await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins that the chat header names why an agent a daemon restart could not bring back stopped.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_interrupted_agent_shows_its_reason_in_the_chat_status() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Resuming);

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted", Interruption = "worktree_missing" });

                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Interrupted);
                await Assert.That(h.Chat.AgentStatus.Tip).Contains("worktree_missing");
                await Assert.That(h.Chat.AgentStatus.Tip).Contains("kcap agent resume a1");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins a terminal select dialog as a chat question: text is held back, and a choice moves the
    /// dialog's cursor from where the screen shows it and confirms, in one write.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_is_answered_by_moving_its_cursor() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var dialog = new TerminalDialogDto("New MCP server found in this project: avalonia-docs",
                "MCP servers may execute code or access system resources.", [
                    "Use this MCP server",
                    "Use this and all future MCP servers in this project",
                    "Continue without using this MCP server",
                ], selected: 2, screen: "");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });
                h.Chat.ComposerText = "hello";

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuTitle).IsEqualTo("Waiting for you in Terminal");
                await Assert.That(h.Chat.TerminalMenuHeading).IsEqualTo(dialog.Heading);
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

                await h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\x1b[A\x1b[A\r" }, CollectionOrdering.Matching);

                var redrawn = new TerminalDialogDto(dialog.Heading, dialog.Body, dialog.Options, selected: 0, screen: "");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = redrawn });
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.HasTerminalMenu).IsFalse();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_approval_pauses_working_until_the_dialog_closes() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                var agent = Agent("a1", "claude", hasTerminal: true) with {
                    Status = "Running", AwaitingInput = false,
                    WorkingSince = h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(65),
                };
                await h.PushAsync(agent);
                h.Time.Advance(TimeSpan.FromSeconds(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 15s");

                h.Chat.ComposerText = "hello";
                var dialog = new TerminalDialogDto("Bash command", "", [], -1,
                    "Bash command\ngrep -ril \"pages/\" README.md\nThis command requires approval\nDo you want to proceed?");
                await h.PushAsync(agent with { TerminalDialog = dialog });

                await Assert.That(h.Chat.HasPendingCards).IsFalse();
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuTitle).IsEqualTo("Waiting for you in Terminal");
                await Assert.That(h.Chat.TerminalMenuScreen).IsEqualTo(dialog.Screen);
                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
                await Assert.That(h.Chat.ActivityNote).IsEmpty();
                await Assert.That(h.Chat.ShowsLiveNote).IsFalse();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

                h.Time.Advance(TimeSpan.FromMinutes(10));
                await h.PushAsync(agent);

                await Assert.That(h.Chat.ShowsTerminalMenu).IsFalse();
                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Working);
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 15s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 17s");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins the fallback for a dialog whose choices could not be read: the card carries the
    /// screen text and sends the user to the terminal, and offers Escape to close it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_without_readable_choices_points_at_the_terminal() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            var shown = false;
            h.Chat.ShowTerminal = () => shown = true;
            var dialog = new TerminalDialogDto("", "", [], -1, "3 new MCP servers found in this project\n❯ [ ] docs\n  [ ] search");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuChoices.Single().Label).IsEqualTo("Close");
                await Assert.That(h.Chat.TerminalMenuScreen).IsEqualTo(dialog.Screen);
                await h.Chat.ShowTerminalCommand.Execute().ToTask();
                await Assert.That(shown).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments("/status", true, true)]
    [Arguments("/cost", true, true)]
    [Arguments("/status", null, false)]
    [Arguments("/status", false, false)]
    [Arguments("hello", true, false)]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_receipts_only_the_latest_idle_command(string text, bool? awaitingInput, bool retired) => RunOnUiAsync(async () => {
        var input = new KeyRecordingInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = awaitingInput };
            await h.PushAsync(agent);
            h.Chat.ComposerText = text;
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.HasQueuedMessages).IsEqualTo(!retired);
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_receipt_can_arrive_before_transport_acceptance() => RunOnUiAsync(async () => {
        var input = new ScriptedInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = true };
            await h.PushAsync(agent);
            h.Chat.ComposerText = "/status";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the send awaiting acceptance");
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_in_another_transcript_does_not_receipt_an_earlier_command(bool arrivesWithSwitch) => RunOnUiAsync(async () => {
        var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
        try {
            var first = Tmp.CreateFile("panel-first.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(first) with { AwaitingInput = true });
            h.Chat.ComposerText = "/status";
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            var next = Tmp.CreateFile("panel-next.jsonl", [AssistantLine]);
            var agent = Dto(next) with { AwaitingInput = true };
            if (!arrivesWithSwitch) await h.PushAsync(agent);
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_does_not_receipt_an_older_command_after_another_send() => RunOnUiAsync(async () => {
        var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = true };
            await h.PushAsync(agent);
            foreach (var text in new[] { "/status", "hello" }) {
                h.Chat.ComposerText = text;
                await h.Chat.SendCommand.Execute().ToTask();
            }
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(2);
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_screen_only_dialog_can_be_closed_once_with_escape() => RunOnUiAsync(async () => {
        var input = new KeyRecordingInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with {
                Status = "Running", TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310")
            });
            var close = h.Chat.TerminalMenuChoices.Single();
            await Assert.That(close.Label).IsEqualTo("Close");
            await close.Choose.Execute().ToTask();
            await Assert.That(input.Writes).IsEquivalentTo(new[] { "\x1b" }, CollectionOrdering.Matching);
            await Assert.That(await close.Choose.CanExecute.FirstAsync()).IsFalse();
        } finally { await h.TeardownAsync(); }
    });

    /// A launch prompt no turn followed may still sit in the composer, so the empty chat says so and
    /// keeps pointing at the terminal, until a turn is reported.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_empty_chat_says_when_the_first_message_may_not_have_been_sent() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                h.Chat.ShowTerminal = () => { };
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", LaunchPromptUnconfirmed = true });
                await Assert.That(h.Chat.PhaseNote).IsEqualTo(ChatTabViewModel.LaunchPromptUnconfirmedNote);
                await Assert.That(h.Chat.ShowsTerminalOffer).IsTrue();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", LaunchPromptUnconfirmed = false });
                await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for chat…");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A resumed chat opens with its history, which hides the empty-chat note, so the warning takes
    /// the activity row: a prompt held there would otherwise leave no sign at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chat_with_history_says_when_the_first_message_may_not_have_been_sent() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                var dto = Dto(Tmp.CreateFile("resumed.jsonl", [UserLine, AssistantLine]));
                await h.PushAsync(dto with { Status = "Running", LaunchPromptUnconfirmed = true });
                await WaitUntilAsync(() => h.Chat.Items.Count > 0, what: "the resumed history");

                await Assert.That(h.Chat.PhaseNote).IsEmpty();
                await Assert.That(h.Chat.ShowsActivityNote).IsTrue();
                await Assert.That(h.Chat.ActivityNote).IsEqualTo(ChatTabViewModel.LaunchPromptUnconfirmedNote);
                await Assert.That(h.Chat.ShowsActivityTerminalOffer).IsFalse();

                h.Chat.ShowTerminal = () => { };
                await Assert.That(h.Chat.ShowsActivityTerminalOffer).IsTrue();
                await Assert.That(h.Chat.ShowsTerminalOffer).IsFalse();

                await h.PushAsync(dto with { Status = "Running", LaunchPromptUnconfirmed = false });

                await Assert.That(h.Chat.ShowsActivityNote).IsFalse();
                await Assert.That(h.Chat.ActivityNote).IsEmpty();
                await Assert.That(h.Chat.ShowsActivityTerminalOffer).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// An empty chat can mean the agent is held at a prompt only its terminal shows, so it points
    /// there, unless the agent has no terminal or the dialog card already does. The bound empty
    /// state must be re-raised when the card appears, or the stale offer stays on screen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_empty_chat_offers_the_terminal_until_the_dialog_card_does() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                await Assert.That(h.Chat.ShowsTerminalOffer).IsFalse();
                h.Chat.ShowTerminal = () => { };
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for chat…");
                await Assert.That(h.Chat.ShowsTerminalOffer).IsTrue();
                await Assert.That(h.Chat.EmptyState!.HasAction).IsTrue();

                var raised = new List<string?>();
                h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(h.Chat.ShowsTerminalOffer).IsFalse();
                await Assert.That(raised).Contains(nameof(ChatTabViewModel.EmptyState));
                await Assert.That(h.Chat.EmptyState!.HasAction).IsFalse();
                await Assert.That(h.Chat.EmptyState!.HasDetail).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins the hook card's precedence: while a card asks the question, the screen's copy of the
    /// same dialog is not offered, and the composer stays shut because that dialog is still on
    /// the terminal. The dialog comes back only once it outlasts the card by the grace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_card_hides_the_terminal_dialog_it_raised() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
            var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
            try {
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                h.Chat.ComposerText = "hello";
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });

                await Assert.That(h.Chat.ShowsTerminalMenu).IsFalse();
                await Assert.That(h.Chat.TerminalMenuChoices).IsEmpty();
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ShowsTerminalMenu).IsFalse();
                h.Time.Advance(ChatTabViewModel.CoveredDialogGrace);
                await WaitUntilAsync(() => h.Chat.ShowsTerminalMenu, what: "the dialog revealed");
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(h.Chat.TerminalMenuChoices).Count().IsEqualTo(2);
            } finally { await h.TeardownAsync(); }
        });
    }

    /// The screen reports an answered card's dialog for a snapshot after the card goes; that copy
    /// must not open the terminal card, nor may the stale grace hide a different dialog after it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_answered_card_does_not_flash_the_dialog_it_covered() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
            var dialog = new TerminalDialogDto("Bash command", "", [], -1, "Do you want to proceed?");
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog };
            try {
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the card");
                await h.PushAsync(agent);

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card answered");
                await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Bash command", "", [], -1, "redrawn") });
                await Assert.That(h.Chat.ShowsTerminalMenu).IsFalse();

                await h.PushAsync(agent with { TerminalDialog = null });
                var next = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
                await h.PushAsync(agent with { TerminalDialog = next });
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
                h.Time.Advance(ChatTabViewModel.CoveredDialogGrace);
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_stays_answered_when_a_pending_card_hides_and_reveals_it() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog };
            try {
                await h.PushAsync(agent);
                await h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards && !h.Chat.ShowsTerminalMenu, what: "the dialog hidden");
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                h.Time.Advance(ChatTabViewModel.CoveredDialogGrace);
                await WaitUntilAsync(() => h.Chat.ShowsTerminalMenu, what: "the dialog revealed");

                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\r" }, CollectionOrdering.Matching);

                await h.PushAsync(agent with { TerminalDialog = null });
                await h.PushAsync(agent);
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsTrue();
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\r", "\x1b[B\r" }, CollectionOrdering.Matching);
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A usage-limit menu is itself a select dialog, so the screen can report both. The dialog's
    /// cursor moving under an answered question must not offer the question's choices again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_answered_usage_limit_stays_answered_while_its_dialog_redraws() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit · resets 3:10pm",
                "What do you want to do?", [new(1, "Stop and wait for limit to reset"), new(2, "Ask your admin for more usage")]);
            static TerminalDialogDto DialogAt(int selected) =>
                new("What do you want to do?", "", ["Stop and wait for limit to reset", "Ask your admin for more usage"], selected, "");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice, TerminalDialog = DialogAt(0) });
                await Assert.That(h.Chat.TerminalMenuPrompt).IsEqualTo(notice.Prompt);

                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice, TerminalDialog = DialogAt(1) });

                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'2' }, CollectionOrdering.Matching);
            } finally { await h.TeardownAsync(); }
        });
    }

    sealed class RecordingChatInput : AcceptingChatInput {
        public List<string> Sent { get; } = [];
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
            Sent.Add(text);
            return Task.FromResult(ChatSendOutcome.Accepted);
        }
    }

    sealed class KeyRecordingInput : AcceptingChatInput {
        public List<byte> Keys { get; } = [];
        public List<string> Writes { get; } = [];
        public bool Accept { get; set; } = true;
        public Task? Gate { get; set; }
        public override bool CanSendKeys => true;
        public override async Task<bool> SendKeysAsync(byte[] keys, CancellationToken ct) {
            if (Gate is { } gate) await gate;
            if (!Accept) return false;
            Keys.AddRange(keys);
            Writes.Add(System.Text.Encoding.ASCII.GetString(keys));
            return true;
        }
    }

    /// Pins the remote card: the server relays the dialog a hosted agent shows, and a channel that
    /// cannot type into it shows the dialog as drawn and sends the viewer to the terminal.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_terminal_dialog_sends_the_viewer_to_the_terminal() {
        await RunOnUiAsync(async () => {
            var dto = new AgentInstanceDto {
                AgentId = "a1", Status = "running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude",
                RegisteredAt = DateTime.UtcNow,
                TerminalDialog = new AgentTerminalDialog {
                    Heading = "Allow external CLAUDE.md file imports?",
                    Options = ["No, disable external imports", "Yes, allow external imports"], Selected = 0,
                    Screen = "Allow external CLAUDE.md file imports?\n❯ No, disable external imports\n  Yes, allow external imports",
                },
            };
            using var session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(AgentRow.FromRemote(dto), ended: false));
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(), null, new RecordingOpener(), time,
                new FakePermissionService(), new SessionRuns(time));
            var shown = false;
            chat.ShowTerminal = () => shown = true;
            try {
                await Assert.That(chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(chat.TerminalMenuTitle).IsEqualTo("Waiting for you in Terminal");
                await Assert.That(chat.TerminalMenuScreen).IsEqualTo(dto.TerminalDialog.Screen);
                await Assert.That(chat.TerminalMenuChoices).IsEmpty();
                await chat.ShowTerminalCommand.Execute().ToTask();
                await Assert.That(shown).IsTrue();

                session.OnNext(ChatSessionInfo.FromRemote(AgentRow.FromRemote(dto with { TerminalDialog = null }), ended: false));
                await Assert.That(chat.HasTerminalMenu).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }
}
