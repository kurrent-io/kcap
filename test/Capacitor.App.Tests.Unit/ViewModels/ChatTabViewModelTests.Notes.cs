using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Lucide.Avalonia;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// Pins the system-note row: a task notification the vendor injects into the transcript
    /// renders as a system note carrying its summary and result, never as a user bubble. It starts
    /// folded, its summary in the header, and opens to the whole result.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_notification_renders_as_a_folded_background_task_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""",
            ]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(SystemNoteItem) });
            var note = (SystemNoteItem)h.Chat.Items[0];
            await Assert.That(note.Text).IsEqualTo("**Agent finished**\n\nAll good.");
            await Assert.That(note.Label).IsEqualTo(SystemNoteItem.TaskNotificationLabel);
            await Assert.That(note.IsExpanded).IsFalse();
            await Assert.That(note.HeaderLabel).IsEqualTo("Background task · Agent finished");
            note.Toggle();
            await Assert.That(note.IsExpanded).IsTrue();
            await Assert.That(note.HeaderLabel).IsEqualTo(SystemNoteItem.TaskNotificationLabel);
            await h.TeardownAsync();
        });
    }

    /// Another session's message reads as an agent's, open under its sender's name, with none of its envelope left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_message_from_another_session_renders_as_an_open_agent_note_under_its_sender() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"<cross-session-message from=\"uds:/tmp/cc-socks/71031.sock\" from-name=\"quiet-cow-d1\" from-mode=\"prompting\">\nOne more symptom.\n\n- `declare_work_item` refuses the new name\n</cross-session-message>","isMeta":true,"origin":{"kind":"peer","name":"quiet-cow-d1"}}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.Text).IsEqualTo("One more symptom.\n\n- `declare_work_item` refuses the new name");
            await Assert.That(note.HeaderLabel).IsEqualTo("Message from quiet-cow-d1");
            await Assert.That(note.IconKind).IsEqualTo(LucideIconKind.BotMessageSquare);
            await Assert.That(note.IsExpanded).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// The agent's icon is for agents: a trigger with no session behind it stays a plain note.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_ci_event_stays_a_plain_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","message":{"content":"<ci-monitor-event>Check `build` failed.</ci-monitor-event>"}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.HeaderLabel).IsEqualTo("CI event");
            await Assert.That(note.IconKind).IsEqualTo(LucideIconKind.StickyNote);
            await h.TeardownAsync();
        });
    }

    /// A notification with only a summary has nothing behind it to fold, so it stays open.
    [Test]
    public async Task A_one_line_note_stays_open_and_a_longer_report_folds_under_its_first_line() {
        var bare = new SystemNoteItem("**MCP task done.**", SystemNoteItem.TaskNotificationLabel);
        await Assert.That(bare.IsCollapsible).IsFalse();
        await Assert.That(bare.IsExpanded).IsTrue();
        await Assert.That(bare.HeaderLabel).IsEqualTo(SystemNoteItem.TaskNotificationLabel);

        var report = new SystemNoteItem("\n## Findings for `Auth`\n\nNone.", SystemNoteItem.AgentReportLabel);
        await Assert.That(report.IsCollapsible).IsTrue();
        await Assert.That(report.IsExpanded).IsFalse();
        await Assert.That(report.HeaderLabel).IsEqualTo("Subagent report · Findings for Auth");
        await Assert.That(report.ToggleAccessibleName).IsEqualTo("Expand Subagent report · Findings for Auth");
        report.Toggle();
        await Assert.That(report.ToggleAccessibleName).IsEqualTo("Collapse Subagent report");

        foreach (var emphasised in new[] { "*Finished task*", "_Finished task_", "- **Finished task**" })
            await Assert.That(new SystemNoteItem(emphasised + "\n\nBody.", SystemNoteItem.AgentReportLabel).HeaderLabel)
                .IsEqualTo("Subagent report · Finished task");

        foreach (var fenced in new[] { "---\n```text\n\nFindings\n```\n\nMore.", "~~~text\nFindings\n~~~\nMore." })
            await Assert.That(new SystemNoteItem(fenced, SystemNoteItem.AgentReportLabel).HeaderLabel)
                .IsEqualTo("Subagent report · Findings");

        foreach (var (literal, peek) in new[] { ("-10% change in throughput", "-10% change in throughput"), ("#123 is fixed", "#123 is fixed"),
                     ("> ## Summary", "Summary"), ("* item", "item"), ("+ item", "item"), ("- # Findings", "Findings"), ("# - Findings", "Findings"), (">  ## Findings", "Findings"), ("Inspect `src/**/test.cs`", "Inspect src/**/test.cs"), ("**Run `__init__`**", "Run __init__"), ("Inspect ``src/**/test.cs``", "Inspect src/**/test.cs"),
                     ("Inspect `src/**/test.cs", "Inspect `src/**/test.cs"), ("Inspect src/**/test.cs", "Inspect src/**/test.cs"),
                     ("Ran **all** the __unit__ tests", "Ran all the unit tests"), ("Check user__id__ field", "Check user__id__ field"),
                     ("Saw \uE0000\uE001 in **`x`**", "Saw \uE0000\uE001 in x"), ("Observed \uE000 in `**secret**`", "Observed \uE000 in **secret**") })
            await Assert.That(new SystemNoteItem(literal + "\nMore.", SystemNoteItem.AgentReportLabel).HeaderLabel)
                .IsEqualTo($"Subagent report · {peek}");

        foreach (var ruled in new[] { "* * *", "- - -", "> ---", "> - - -", ">  ---", "> > ---" })
            await Assert.That(new SystemNoteItem(ruled + "\nFindings\nMore.", SystemNoteItem.AgentReportLabel).HeaderLabel)
                .IsEqualTo("Subagent report · Findings");

        foreach (var lone in new[] { "---\n\n**Done.**", "---\n```text\n\nFindings\n```", "Findings\n~~~\n---" })
            await Assert.That(new SystemNoteItem(lone, SystemNoteItem.AgentReportLabel).IsCollapsible).IsFalse();

        var other = new SystemNoteItem("Reconnected.\nResumed the session.");
        await Assert.That(other.IsCollapsible).IsFalse();
        await Assert.That(other.IsExpanded).IsTrue();
    }

    /// Claude Code stores its compaction summary as a user record; the row must not read as the user's,
    /// and it starts folded because it restates the conversation above it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_compaction_summary_renders_as_a_conversation_compacted_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","isCompactSummary":true,"isVisibleInTranscriptOnly":true,"message":{"content":"This session is being continued from a previous conversation."}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.Label).IsEqualTo(SystemNoteItem.CompactSummaryLabel);
            await Assert.That(note.Text).IsEqualTo("This session is being continued from a previous conversation.");
            await Assert.That(note.IsExpanded).IsFalse();
            await Assert.That(note.ToggleAccessibleName).IsEqualTo("Expand Conversation compacted");
            note.Toggle();
            await Assert.That(note.IsExpanded).IsTrue();
            await Assert.That(note.ToggleAccessibleName).IsEqualTo("Collapse Conversation compacted");
            note.Toggle();
            await Assert.That(note.IsExpanded).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Neither a CI event nor an interrupt is the user's: each is a note named for what it is, and an
    /// interrupt, which is only its label, has no body. A slash command is the user's own row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_trigger_and_an_interrupt_render_as_labelled_notes_and_a_command_as_the_users() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","message":{"content":"<command-name>/goal</command-name><command-args>merge PR #4577</command-args>"}}""",
                """{"type":"user","message":{"content":"<ci-monitor-event>Check `build` failed.</ci-monitor-event>"}}""",
                """{"type":"user","message":{"content":"[Request interrupted by user]"}}""",
            ]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Single().Text).IsEqualTo("/goal merge PR #4577");
            var notes = h.Chat.Items.OfType<SystemNoteItem>().ToArray();
            await Assert.That(notes.Select(n => n.Label)).IsEquivalentTo(new[] { "CI event", SystemNoteItem.InterruptedLabel });
            await Assert.That(notes[0].Text).IsEqualTo("Check `build` failed.");
            await Assert.That(notes[0].ShowsBody).IsTrue();
            await Assert.That(notes[1].ShowsBody).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A subagent's hand-back is that agent's report, so its row names the agent rather than the user.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_subagent_handback_renders_as_a_subagent_report() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"<agent-message from=\"a1\">\n[Subagent hand-back] Preamble. The report follows:\n  **Done.**\n</agent-message>","origin":{"kind":"peer","from":"a1"}}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.Label).IsEqualTo(SystemNoteItem.AgentReportLabel);
            await Assert.That(note.IconKind).IsEqualTo(Lucide.Avalonia.LucideIconKind.BotMessageSquare);
            await Assert.That(note.Text).IsEqualTo("**Done.**");
            await Assert.That(note.IsExpanded).IsTrue();
            note.Toggle();
            await Assert.That(note.IsExpanded).IsTrue();
            await h.TeardownAsync();
        });
    }
}
