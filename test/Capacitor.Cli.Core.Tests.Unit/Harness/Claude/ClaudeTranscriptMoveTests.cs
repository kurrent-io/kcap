using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// A transcript gone from its project folder is found under the sibling folder it moved to, for a session and its subagents alike.
public class ClaudeTranscriptMoveTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Find_returns_the_session_transcript_under_its_new_project_folder() {
        var lost  = Tmp.PathTo("projects", "-repo", "s1.jsonl");
        var moved = Tmp.CreateFile(["projects", "-repo--claude-worktrees-w1", "s1.jsonl"], "{}\n");
        Tmp.CreateFile(["projects", "-other", "s2.jsonl"], "{}\n");

        await Assert.That(ClaudeTranscriptMove.Find(lost)).IsEqualTo(moved);
    }

    [Test]
    public async Task Find_returns_a_subagent_transcript_under_its_new_project_folder() {
        var lost  = Tmp.PathTo("projects", "-repo", "s1", "subagents", "agent-a1.jsonl");
        var moved = Tmp.CreateFile(["projects", "-repo--claude-worktrees-w1", "s1", "subagents", "agent-a1.jsonl"], "{}\n");

        await Assert.That(ClaudeTranscriptMove.Find(lost)).IsEqualTo(moved);
    }

    [Test]
    public async Task Find_prefers_the_most_recently_written_copy() {
        var lost  = Tmp.PathTo("projects", "-repo", "s1.jsonl");
        var older = Tmp.CreateFile(["projects", "-repo--claude-worktrees-w1", "s1.jsonl"], "{}\n");
        var newer = Tmp.CreateFile(["projects", "-repo--claude-worktrees-w2", "s1.jsonl"], "{}\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));

        await Assert.That(ClaudeTranscriptMove.Find(lost)).IsEqualTo(newer);
    }

    [Test]
    public async Task Find_returns_null_when_no_project_folder_holds_the_transcript() {
        Tmp.CreateFile(["projects", "-other", "s2.jsonl"], "{}\n");

        await Assert.That(ClaudeTranscriptMove.Find(Tmp.PathTo("projects", "-repo", "s1.jsonl"))).IsNull();
    }
}
