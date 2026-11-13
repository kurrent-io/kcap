using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Models.Transcripts.Harness.Claude;
using DynamicData;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    const string ThinkingLine = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"weighing it"}]}}""";
    const string PlanCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_P","name":"mcp__plugin_kcap_kcap-plans__update_plan_task","input":{"ordinal":1,"status":"completed"}}]}}""";
    const string PlanResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_P","content":"{}"}]}}""";
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>","visibility":"org"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
    const string WorkItemCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_wi","name":"mcp__plugin_kcap_kcap-workitems__declare_work_item","input":{"issue_key":"WK-3084"}}]}}""";
    const string LinearCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_lin","name":"mcp__plugin_linear_linear__save_issue","input":{"title":"Add tests"}}]}}""";
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_message_link_goes_to_the_session_link_handler_instead_of_the_browser() {
        await RunOnUiAsync(async () => {
            var routed = new List<string>();
            var h = new Harness(TranscriptChat.For("claude"), openLink: routed.Add);

            await h.Chat.OpenLinkCommand.Execute("https://a.kcap.ai/pages/abc");

            await Assert.That(routed).IsEquivalentTo(new[] { "https://a.kcap.ai/pages/abc" });
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_already_cached_when_the_tab_opens_lights_the_row_at_once() {
        await RunOnUiAsync(async () => {
            var h = Claude(seed: p => p.Add(PermissionEntries.Entry("r1", "a1")));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the replayed card");
            await Assert.That(h.Chat.HasPendingCards).IsTrue();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1" });
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_closing_local_tab_hands_off_its_feed_whether_or_not_it_reads_through_the_shared_cache(bool shared) {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), transcripts: shared ? new LocalTranscripts() : null);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine])));
            await WaitUntilAsync(() => h.Chat.Items.Count > 0, what: "the initial load");

            var handoff = h.Chat.ReleaseFeed();

            await Assert.That(handoff).IsNotNull();
            await Assert.That(handoff!.Runs).IsSameReferenceAs(h.Runs);
            handoff.Feed.Dispose();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Waits_until_a_path_then_renders_the_initial_load_in_file_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for chat…");

            await h.PushAsync(Dto(transcriptPath: null));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);

            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine, ToolCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(Group(h.Chat, 2).Calls[0].Detail).IsEqualTo("ls -la");
            await Assert.That(Group(h.Chat, 2).Calls[0].Outcome).IsEqualTo(ToolOutcome.Running);
            await Assert.That(Group(h.Chat, 2).Calls[0].Category).IsEqualTo(ToolCategory.Command);
            await h.TeardownAsync();
        });
    }

    /// Claude Code prints a thinking block as prose in its own terminal, so the chat shows the same
    /// text in file order and, like any prose, it closes the open tool group.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thinking_block_is_a_row_that_closes_the_open_group() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine, ThinkingLine, ReadCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(ToolGroupItem), nameof(AssistantThinkingItem), nameof(ToolGroupItem) },
                CollectionOrdering.Matching);
            await Assert.That(((AssistantThinkingItem)h.Chat.Items[2]).Text).IsEqualTo("weighing it");
            await Assert.That(Group(h.Chat, 1).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash" }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 3).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    /// Tool paths read relative to the checkout the agent runs in, which the wire names as
    /// worktree_path; the repository alone would leave a checkout outside its own directory, or
    /// one under `.claude/worktrees`, rendering absolute paths.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_paths_relativize_against_the_worktree_the_agent_runs_in() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string worktree = "/repo/x/.claude/worktrees/slug";
            const string readLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/.claude/worktrees/slug/src/Foo.cs"}}]}}""";
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/.claude/worktrees/slug/src/Bar.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");

            var path = Tmp.CreateFile("t.jsonl", [readLine]);
            await h.PushAsync(Dto(path) with { WorktreePath = worktree, WorkLocation = "owned" });

            await Assert.That(Group(h.Chat, 0).Calls.Single().Detail).IsEqualTo("src/Foo.cs");
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/Bar.cs", what: "relative to the worktree once the root lands");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appended_lines_render_after_a_tick_and_a_partial_line_waits_for_its_newline() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine[..20]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolCallLine[20..] + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(3);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_results_flip_their_call_in_place_and_unmatched_results_are_ignored() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));
            var call = Group(h.Chat, 0).Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await Assert.That(Group(h.Chat, 0).Calls[1].Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(Group(h.Chat, 0).Calls[1].IsError).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task An_unresolved_tool_in_an_ended_session_has_an_unknown_outcome(bool alreadyEnded) => RunOnUiAsync(async () => {
        var h = Claude();
        try {
            var path = Tmp.CreateFile("unresolved.jsonl", [ToolCallLine, ReadCallLine, ToolResultLine.Replace("t1", "t2")]);
            var dto = Dto(path);
            await h.PushAsync(alreadyEnded ? dto with { Status = "Completed" } : dto);
            var group = Group(h.Chat, 0);
            var call = group.Calls[0];
            call.Toggle();
            if (!alreadyEnded) {
                await Assert.That(call.IsRunning).IsTrue();
                await Assert.That(call.Inspection!.Sections[0].Text).IsEqualTo("Running");
                call.IsAwaitingPermission = true;
                await h.PushAsync(dto with { Status = "Completed" });
            }

            await Assert.That(call.IsRunning).IsFalse();
            await Assert.That(call.IsAwaitingPermission).IsFalse();
            await Assert.That(call.Inspection!.Sections[0].Text).IsEqualTo("Outcome unknown");
            await Assert.That(call.Inspection.Sections[2].Text).IsEqualTo("No result was recorded");
            await Assert.That(call.Inspection.Sections[2].IsMissing).IsTrue();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Running);
            await Assert.That(group.VisibleCalls.Contains(call)).IsTrue();
            await Assert.That(group.Calls[1].Outcome).IsEqualTo(ToolOutcome.Done);

            File.AppendAllText(path, ToolCallLine.Replace("t1", "t3") + "\n");
            await h.TickAsync();
            await Assert.That(group.Calls[2].IsRunning).IsFalse();

            call.IsAwaitingPermission = false;
            await h.PushAsync(dto);
            await Assert.That(call.IsRunning).IsTrue();
            await Assert.That(call.Inspection!.Sections[0].Text).IsEqualTo("Running");
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task An_unresolved_tool_in_an_ended_session_accepts_a_late_result(bool error) => RunOnUiAsync(async () => {
        var h = Claude();
        try {
            var path = Tmp.CreateFile("late-result.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path) with { Status = "Completed" });
            var call = Group(h.Chat, 0).Calls.Single();
            call.Toggle();
            await Assert.That(call.Inspection!.Sections[0].Text).IsEqualTo("Outcome unknown");

            File.AppendAllText(path, (error ? ToolErrorLine : ToolResultLine) + "\n");
            await h.TickAsync();

            await Assert.That(Group(h.Chat, 0).Calls.Single()).IsSameReferenceAs(call);
            await Assert.That(call.IsRunning).IsFalse();
            await Assert.That(call.IsSettled).IsTrue();
            await Assert.That(call.Inspection!.Sections[0].Text).IsEqualTo(error ? "Failed" : "Finished");
            await Assert.That(call.Inspection.Sections[2].Text).IsEqualTo(error ? "boom" : "ok");
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Length_regression_resets_items_missing_recovers_and_failed_keeps_items() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.PathTo("t.jsonl");
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Missing);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("The transcript file is missing");

            File.WriteAllLines(path, [UserLine, AssistantLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(h.Chat.Items[0]).IsTypeOf<ToolGroupItem>();

            File.Delete(path);
            Directory.CreateDirectory(path);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            Directory.Delete(path);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Consecutive_calls_across_reads_share_a_group_and_any_prose_closes_it() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash", "Read" }, CollectionOrdering.Matching);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine.Replace("t1", "t3") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 2).Calls).Count().IsEqualTo(1);

            File.AppendAllText(path, NoteLine + "\n" + ToolCallLine.Replace("t1", "t4") + "\n" + UserLine + "\n" + ToolCallLine.Replace("t1", "t5") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] {
                nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem),
                nameof(SystemNoteItem), nameof(ToolGroupItem), nameof(UserTurnItem), nameof(ToolGroupItem),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_folds_its_call_and_the_summary_follows_the_settled_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var group = Group(h.Chat, 0);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" });
            await Assert.That(group.Summary).IsEqualTo("Ran a command");
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.HasFailure).IsFalse();

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(1);

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Ran a command, read a file");
            await Assert.That(group.HasFailure).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// Prose closes the group while the call stays pending. The result arrives in a later read,
    /// and the row it settles is already bound, so that update has to run on the UI thread.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_result_settles_the_closed_groups_call_on_the_ui_thread() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            var call = Group(h.Chat, 0).Calls[0];
            var onUiThread = false;
            call.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(ToolCallItem.Outcome))
                    onUiThread = Dispatcher.UIThread.CheckAccess();
            };

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();

            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(onUiThread).IsTrue();
            await Assert.That(Group(h.Chat, 0).Calls[0]).IsSameReferenceAs(call);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_and_a_path_switch_start_a_fresh_group_for_later_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(1);
            File.AppendAllText(other, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_kcap_call_reads_as_a_labelled_row_and_a_publish_becomes_a_card_when_its_result_lands() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [WorkItemCallLine, PublishCallLine]);
            await h.PushAsync(Dto(path));

            var group = Group(h.Chat, 0);
            var work = group.Calls[0];
            await Assert.That(work.Label).IsEqualTo("Attached work item");
            await Assert.That(work.Detail).IsEqualTo("WK-3084");
            await Assert.That(work.Category).IsEqualTo(ToolCategory.Work);
            var publish = group.Calls[1];
            await Assert.That(publish.Label).IsEqualTo("Published page");
            await Assert.That(publish.Detail).IsEqualTo("Retention brief");
            await Assert.That(publish.HasCard).IsFalse();

            File.AppendAllText(path, PublishResultLine + "\n");
            await h.TickAsync();

            await Assert.That(publish.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(publish.HasCard).IsTrue();
            await Assert.That(publish.Card!.Meta).IsEqualTo("v1 · Org");
            await Assert.That(publish.CanOpenCard).IsTrue();
            await publish.OpenCardCommand!.Execute();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://kurrent.kcap.ai/artefacts/01ec" });
            await h.TeardownAsync();
        });
    }

    /// A described command keeps its description on the row under the same verb as any command,
    /// and opening it shows the command and the output the transcript recorded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_described_command_row_keeps_its_description_and_opens_on_the_recorded_command_and_output() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var call = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rm -rf node_modules && npm ci","description":"Reinstall dependencies"}}]}}""";
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [call, ToolErrorLine])));

            var row = Group(h.Chat, 0).Calls[0];
            await Assert.That(row.Label).IsEqualTo("Ran command");
            await Assert.That(row.Detail).IsEqualTo("Reinstall dependencies");
            await Assert.That(row.Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(row.Result).IsEqualTo("boom");

            row.Toggle();
            var sections = row.Inspection!.Sections;
            await Assert.That(sections.Select(s => (s.Heading, s.Text))).IsEquivalentTo(new[] {
                ("Status", "Failed"), ("Command", "rm -rf node_modules && npm ci"), ("Output", "boom"),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [Arguments("mcp__plugin_linear_linear__save_issue", "Linear · save issue", "Linear")]
    [Arguments("mcp__claude_ai_Linear__save_issue", "Linear · save issue", "Linear")]
    [Arguments("mcp__claude_ai_kcap-plans__some_tool", "Kcap plans · some tool", "Kcap plans")]
    [Arguments("mcp__claude_ai_kcap-plans__declare_plan_document", "Kcap plans · declare plan document", "Kcap plans")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_foreign_mcp_call_reads_as_server_and_tool_with_its_first_argument(string name, string label, string chip) {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var line = LinearCallLine.Replace("mcp__plugin_linear_linear__save_issue", name, StringComparison.Ordinal);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [line])));

            var group = Group(h.Chat, 0);
            var call = group.Calls[0];
            await Assert.That(group.KindChip).IsEqualTo(chip);
            await Assert.That(call.CardKind).IsEqualTo(ToolCardKind.None);
            await Assert.That(call.Label).IsEqualTo(label);
            await Assert.That(call.Detail).IsEqualTo("Add tests");
            await Assert.That(call.Category).IsEqualTo(ToolCategory.Other);
            await h.TeardownAsync();
        });
    }

    /// A page card follows the opener the workspace hands in and bypasses the browser.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_injected_card_opener_takes_precedence_over_the_browser() {
        await RunOnUiAsync(async () => {
            ToolCard? opened = null;
            var h = Claude(openCard: card => opened = card);
            var path = Tmp.CreateFile("t.jsonl", [PublishCallLine, PublishResultLine]);
            await h.PushAsync(Dto(path));

            var publish = Group(h.Chat, 0).Calls[0];
            await publish.OpenCardCommand!.Execute();
            await Assert.That(opened?.Url).IsEqualTo("https://kurrent.kcap.ai/artefacts/01ec");
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_plan_write_in_the_transcript_is_reported_once_its_result_is_read() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var writes = 0;
            h.Plan.PlanWritten += () => writes++;
            var path = Tmp.CreateFile("t.jsonl", [PlanCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(writes).IsEqualTo(0);

            File.AppendAllText(path, PlanResultLine + "\n");
            await h.TickAsync();

            await Assert.That(writes).IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_new_transcript_forgets_the_plan_writes_the_old_one_left_in_flight() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var writes = 0;
            h.Plan.PlanWritten += () => writes++;
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [PlanCallLine])));

            var other = Tmp.CreateFile("o.jsonl", [PlanResultLine]);
            await h.PushAsync(Dto(other));

            await Assert.That(writes).IsEqualTo(0);
            await Assert.That(h.Plan.FeedKey).IsEqualTo(other);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_discards_a_read_still_in_flight_for_the_old_file() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
            var oldPath = Tmp.CreateFile("old.jsonl", [UserLine.Replace("hello", "OLD"), ToolCallLine]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(AssistantTextItem) });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_for_a_vendor_without_a_projection_and_no_ticks_after_teardown() {
        await RunOnUiAsync(async () => {
            var unavailable = new Harness(projection: null);
            await Assert.That(unavailable.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(unavailable.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await unavailable.TeardownAsync();

            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await h.TeardownAsync();
            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            // A removed agent keeps its items.
            var kept = Claude();
            var keptPath = Tmp.CreateFile("kept.jsonl", [UserLine]);
            await kept.PushAsync(Dto(keptPath));
            kept.Daemon.Agents.Remove("a1");
            await Assert.That(kept.Chat.Items).Count().IsEqualTo(1);
            await kept.TeardownAsync();
        });
    }

    /// Thread identity, so deliberately not under WithImmediateRxScheduler. PhaseNote and the FIRST
    /// notification are what make it discriminating: that one is raised by the path switch, on
    /// whichever thread delivered the dto, while every later one comes from the apply, which hops
    /// to the UI thread on its own and would pass with no ObserveOn at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dto_pushed_from_a_pool_thread_lands_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            bool? phaseChangedOnUi = null;
            h.Chat.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(ChatTabViewModel.PhaseNote))
                    phaseChangedOnUi ??= Dispatcher.UIThread.CheckAccess();
            };

            await Task.Run(() => h.Daemon.Agents.AddOrUpdate(Dto(path)));
            await WaitUntilAsync(() => h.Chat.Phase == ChatTabPhase.Reading, what: "reading");
            await h.TeardownAsync();
            return phaseChangedOnUi;
        });
        await Assert.That(onUi).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_starts_a_fresh_projection_context_and_line_count() {
        await RunOnUiAsync(async () => {
            var counting = new CountingProjection(ClaudeTranscriptEvents.Instance);
            var h = new Harness(new TranscriptChatProjection(counting, ClaudeChatRules.Instance));
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2 });

            Tmp.CreateFile("t.jsonl", [UserLine]);    // shorter: the tail resets
            await h.TickAsync();
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2, 1 });
            await Assert.That(counting.ContextsCreated).IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Journal_file_renders_user_assistant_note_and_tool_rows() {
        await RunOnUiAsync(async () => {
            var path = Tmp.CreateFile("j.jsonl", [
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SessionStarted, Cwd: "/w")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "hello")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: "note")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: "c1", ToolName: "Read", ToolInputJson: """{"file_path":"/w/a.cs"}""")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1", ToolResult: "ok")),
            ]);
            var h = new Harness(TranscriptChat.Journal);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { TranscriptPath = path, TranscriptFormat = TranscriptFormats.Envelopes });
            await h.TickAsync();

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(SystemNoteItem), nameof(ToolGroupItem) },
                CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_note_words_the_older_and_newer_daemon_cases() {
        await RunOnUiAsync(async () => {
            var older = new Harness(null, unavailableNote: "Update the daemon to view this session");
            await Assert.That(older.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(older.Chat.PhaseNote).IsEqualTo("Update the daemon to view this session");
            await older.TeardownAsync();
            var plain = new Harness(null);
            await Assert.That(plain.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await plain.TeardownAsync();
        });
    }

    sealed class CountingProjection(ITranscriptProjection inner) : ITranscriptProjection {
        public List<int> LineNumbers { get; } = [];
        public int ContextsCreated { get; private set; }

        public TranscriptContext CreateContext(string sessionId, string? agentId) {
            ContextsCreated++;
            return inner.CreateContext(sessionId, agentId);
        }

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            LineNumbers.Add(lineNumber);
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }
}
