using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Claude;
using DynamicData;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string ShellCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_S","name":"Bash","input":{"command":"dotnet test","description":"Run the full suite","run_in_background":true}}]}}""";
    const string ShellLaunchLine = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_S","type":"tool_result","content":"Command running in background with ID: bcyix00ks.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"bcyix00ks"}}""";
    const string AgentFinishLine ="""{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_drive_the_strip_singular_and_plural() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.Tasks.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Tasks.HasRows).IsTrue();
            await Assert.That(h.Chat.Tasks.Rows.Single().Name).IsEqualTo("Explore");
            await Assert.That(h.Chat.Tasks.Rows.Single().StateText).StartsWith("running in background · ");
            await Assert.That(h.Chat.Tasks.CountTip).IsEqualTo("1 subagent running");
            await Assert.That(h.Runs.Rows.Single().IsBackground).IsTrue();
            await Assert.That(raised).Contains(nameof(BackgroundTasksViewModel.HasRows));
            await Assert.That(raised).Contains(nameof(BackgroundTasksViewModel.Count));
            await Assert.That(raised).Contains(nameof(BackgroundTasksViewModel.CountTip));

            File.AppendAllText(path, AgentCallLine.Replace("toolu_A", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.Count).IsEqualTo(1);

            File.AppendAllText(path, AgentLaunchLine.Replace("toolu_A", "toolu_B").Replace("a9f262478e032f427", "b2") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.Count).IsEqualTo(2);
            await Assert.That(h.Chat.Tasks.CountTip).IsEqualTo("2 subagents running");

            File.AppendAllText(path, AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.Rows.Single().CallId).IsEqualTo("toolu_B");
            await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);

            File.AppendAllText(path, ToolResultLine.Replace("t1", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await Assert.That(h.Runs.Rows.Select(r => r.State)).IsEquivalentTo(new[] { RunState.Done, RunState.Done }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    /// Background commands and background subagents share one folded group, which keeps them
    /// through a live turn until each ends and folds again once none is left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_put_background_commands_and_subagents_in_one_group() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ShellCallLine, ShellLaunchLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.Tasks.IsExpanded).IsFalse();
            await Assert.That(h.Chat.Tasks.Rows.Single().Name).IsEqualTo("Run the full suite");
            await Assert.That(h.Chat.Tasks.Rows.Single().Description).IsEqualTo("dotnet test");
            await Assert.That(h.Chat.Tasks.CountTip).IsEqualTo("1 command running");

            await h.Chat.Tasks.ToggleCommand.Execute();
            await Assert.That(h.Chat.Tasks.AccessibleName).IsEqualTo("Background tasks, 1 command running, expanded");

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_T") + "\n" + ShellLaunchLine.Replace("toolu_S", "toolu_T").Replace("bcyix00ks", "b2") + "\n"
                + AgentCallLine + "\n" + AgentLaunchLine + "\n");
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Chat.Tasks.Rows.Select(r => r.CallId)).IsEquivalentTo(new[] { "toolu_S", "toolu_T", "toolu_A" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.Tasks.CountTip).IsEqualTo("1 subagent and 2 commands running");

            File.AppendAllText(path, ShellFinishLine + "\n" + AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.Rows.Select(r => r.CallId)).IsEquivalentTo(new[] { "toolu_T" });
            await Assert.That(h.Chat.Tasks.IsExpanded).IsTrue();

            File.AppendAllText(path, ShellFinishLine.Replace("toolu_S", "toolu_T").Replace("bcyix00ks", "b2") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await Assert.That(h.Chat.Tasks.IsExpanded).IsFalse();

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_F") + "\n" + ToolResultLine.Replace("t1", "toolu_F") + "\n");
            await h.TickAsync();
            await Assert.That(h.Runs.Rows.Any(r => r.CallId == "toolu_F")).IsFalse();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A foreground launch is the turn's own Task row in the transcript, so the band never lists
    /// it, while its turn is live or after.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_leaves_a_foreground_run_to_the_transcript() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Runs.Running).Count().IsEqualTo(1);
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();

            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEmpty();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The next prompt starts a live turn; a subagent still going in the background stays in the
    /// band until it ends, as it stays in the sidebar.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_background_run_stays_in_the_strip_through_the_next_turn() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.Tasks.HasRows).IsTrue();

            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Chat.Tasks.Rows.Single().CallId).IsEqualTo("toolu_A");

            File.AppendAllText(path, AgentCallLine.Replace("toolu_A", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.Rows.Single().CallId).IsEqualTo("toolu_A");
            await Assert.That(h.Chat.Tasks.Count).IsEqualTo(1);

            File.AppendAllText(path, AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A detach changes no state count, yet it is what brings a mid-turn run into the strip.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_run_sent_to_the_background_mid_turn_joins_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.Tasks.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();

            raised.Clear();
            File.AppendAllText(path, AgentLaunchLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsTrue();
            await Assert.That(h.Chat.Tasks.Rows.Single().StateText).StartsWith("running in background · ");
            await Assert.That(raised).Contains(nameof(BackgroundTasksViewModel.HasRows));
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_feed_reset_and_a_path_switch_empty_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Tasks.HasRows).IsTrue();

            File.WriteAllLines(path, [UserLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await Assert.That(h.Runs.Rows).IsEmpty();

            var other = Tmp.CreateFile("o.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Tasks.HasRows).IsTrue();
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await Assert.That(h.Runs.Rows).IsEmpty();
            await h.TeardownAsync();
        });
    }

    /// The status can land before the first read: rows projected after the session ended present
    /// as stopped at once, and a reset under the ended session rebuilds them stopped too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_status_before_the_first_read_and_a_reset_after_it_leave_no_running_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path) with { Status = "Completed" });
            await Assert.That(h.Runs.Rows).Count().IsEqualTo(1);
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await Assert.That(h.Runs.Rows.Single().StateText).IsEqualTo("stopped");

            File.WriteAllLines(path, [AgentCallLine.Replace("Map desktop chat UI surfaces", "Map")]);
            await h.TickAsync();
            await Assert.That(h.Runs.Rows.Single().Description).IsEqualTo("Map");
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_generation_read_after_a_feed_switch_adds_no_subagent_rows() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
            var oldPath = Tmp.CreateFile("old.jsonl", [AgentCallLine.Replace("Map desktop chat UI surfaces", "OLD")]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Runs.Rows).IsEmpty();
            await Assert.That(h.Chat.Tasks.HasRows).IsFalse();
            await h.TeardownAsync();
        });
    }
}
