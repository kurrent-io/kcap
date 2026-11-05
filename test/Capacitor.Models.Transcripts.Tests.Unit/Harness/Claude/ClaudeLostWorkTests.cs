namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Claude;

public class ClaudeLostWorkTests {
    const string LostAgent = """
        <task-notification>
        <task-id>a10669f5c9b2c160e</task-id>
        <status>stopped</status>
        <summary>Background agent "Implement Task 8" didn't finish before the previous session ended</summary>
        </task-notification>
        """;

    const string LostShells = """
        <task-notification>
        <task-id>b3xjy97b1</task-id>
        <task-id>__orphan_summary__:shell</task-id>
        <status>stopped</status>
        <summary>2 background shell command tasks didn't finish before the previous session ended. Task ids: b3xjy97b1, b57nsrkjt.</summary>
        </task-notification>
        """;

    const string Finished = """
        <task-notification>
        <task-id>ks3zycdo1</task-id>
        <status>completed</status>
        <summary>Background command "dotnet test" completed (exit code 0)</summary>
        </task-notification>
        """;

    static readonly string Turn = new JsonObject {
        ["type"]    = "assistant",
        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "On it" },
    }.ToJsonString();

    [TempDir] public required TempDir Tmp { get; init; }

    string Transcript(params string[] lines) => Tmp.CreateFile($"{Guid.NewGuid():N}.jsonl", string.Join('\n', lines) + "\n");

    static string UserLine(string text, string kind = "task-notification") => new JsonObject {
        ["type"]    = "user",
        ["origin"]  = new JsonObject { ["kind"] = kind },
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    static string Bookkeeping(string type) => new JsonObject { ["type"] = type }.ToJsonString();

    [Test]
    public async Task A_lost_background_agent_is_reported() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent)))).IsTrue();

    [Test]
    public async Task Lost_background_shells_are_reported() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostShells)))).IsTrue();

    [Test]
    public async Task A_task_that_finished_is_not_lost_work() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(Finished)))).IsFalse();

    /// <summary>Something already took the work up, so an agent nudged once is not nudged again.</summary>
    [Test]
    public async Task A_turn_after_the_notice_answers_it() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent), Turn))).IsFalse();

    /// <summary>The vendor writes a notice once, so one no turn has answered still counts at the next restart: going
    /// by when it was written would lose the work to a second restart landing before the nudge.</summary>
    [Test]
    public async Task An_unanswered_notice_still_counts_whatever_the_session_recorded_after_it() {
        var path = Transcript(Turn, UserLine(LostAgent), Bookkeeping("attachment"), Bookkeeping("permission-mode"));

        await Assert.That(ClaudeLostWork.IsReported(path)).IsTrue();
    }

    [Test]
    public async Task A_new_notice_after_an_answered_one_is_reported() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent), Turn, UserLine(LostShells)))).IsTrue();

    /// <summary>A line naming the assistant is not a turn: the filter that skips parsing most lines must not decide.</summary>
    [Test]
    public async Task A_line_that_only_names_the_assistant_answers_nothing() {
        var path = Transcript(UserLine(LostAgent), UserLine("ask the assistant", kind: "human"));

        await Assert.That(ClaudeLostWork.IsReported(path)).IsTrue();
    }

    /// <summary>A person pasting a notice into the chat did not lose anything.</summary>
    [Test]
    public async Task A_person_quoting_a_notice_is_ignored() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent, kind: "human")))).IsFalse();

    [Test]
    public async Task A_notice_behind_a_line_still_being_written_is_found() {
        var path = Transcript(UserLine(LostAgent), """{"type":"user","timestamp":"2026-11-""");

        await Assert.That(ClaudeLostWork.IsReported(path)).IsTrue();
    }

    /// <summary>Only the tail is read, and the cut lands inside a line that must not stop the scan.</summary>
    [Test]
    public async Task A_notice_at_the_end_of_a_long_transcript_is_found() {
        var filler = new JsonObject { ["type"] = "assistant", ["pad"] = new string('x', 64 * 1024) }.ToJsonString();
        var path   = Transcript([.. Enumerable.Repeat(filler, 16), UserLine(LostAgent)]);

        await Assert.That(ClaudeLostWork.IsReported(path)).IsTrue();
    }

    [Test]
    public async Task A_transcript_that_cannot_be_read_reports_nothing() =>
        await Assert.That(ClaudeLostWork.IsReported(Tmp.PathTo("missing.jsonl"))).IsFalse();
}
