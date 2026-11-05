namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Claude;

public class ClaudeLostWorkTests {
    static readonly DateTimeOffset Since = new(2026, 11, 4, 17, 28, 15, TimeSpan.Zero);

    const string After  = "2026-11-04T17:28:30.000Z";
    const string Before = "2026-11-04T17:21:12.000Z";

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

    [TempDir] public required TempDir Tmp { get; init; }

    string Transcript(params string[] lines) => Tmp.CreateFile($"{Guid.NewGuid():N}.jsonl", string.Join('\n', lines) + "\n");

    static string UserLine(string text, string at, string kind = "task-notification") => new JsonObject {
        ["type"]      = "user",
        ["timestamp"] = at,
        ["origin"]    = new JsonObject { ["kind"] = kind },
        ["message"]   = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    [Test]
    public async Task A_lost_background_agent_is_reported() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent, After)), Since)).IsTrue();

    [Test]
    public async Task Lost_background_shells_are_reported() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostShells, After)), Since)).IsTrue();

    [Test]
    public async Task A_task_that_finished_is_not_lost_work() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(Finished, After)), Since)).IsFalse();

    /// <summary>The notice an earlier restart left behind says nothing about this one.</summary>
    [Test]
    public async Task A_notice_from_before_the_stop_is_ignored() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent, Before)), Since)).IsFalse();

    /// <summary>A person pasting a notice into the chat did not lose anything.</summary>
    [Test]
    public async Task A_person_quoting_a_notice_is_ignored() =>
        await Assert.That(ClaudeLostWork.IsReported(Transcript(UserLine(LostAgent, After, kind: "human")), Since)).IsFalse();

    [Test]
    public async Task A_notice_behind_a_line_still_being_written_is_found() {
        var path = Transcript(UserLine(LostAgent, After), """{"type":"user","timestamp":"2026-11-""");

        await Assert.That(ClaudeLostWork.IsReported(path, Since)).IsTrue();
    }

    /// <summary>Only the tail is read, and the cut lands inside a line that must not stop the scan.</summary>
    [Test]
    public async Task A_notice_at_the_end_of_a_long_transcript_is_found() {
        var filler = new JsonObject { ["type"] = "assistant", ["pad"] = new string('x', 64 * 1024) }.ToJsonString();
        var path   = Transcript([.. Enumerable.Repeat(filler, 16), UserLine(LostAgent, After)]);

        await Assert.That(ClaudeLostWork.IsReported(path, Since)).IsTrue();
    }

    [Test]
    public async Task A_transcript_that_cannot_be_read_reports_nothing() =>
        await Assert.That(ClaudeLostWork.IsReported(Tmp.PathTo("missing.jsonl"), Since)).IsFalse();
}
