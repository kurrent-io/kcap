using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class SkillLoadParserTests {
    const string SkillBody =
        "Base directory for this skill: /Users/me/.claude/plugins/cache/superpowers/6.3.0/skills/using-git-worktrees\n\n" +
        "# Using Git Worktrees\n\n## Overview\n\nEnsure work happens in an isolated workspace.";

    /// <summary>A loaded skill's body is the harness talking, shown as one line naming the skill.</summary>
    [Test]
    public async Task A_skill_body_is_a_harness_payload_named_by_its_directory() {
        var harness = UserPromptDisplay.IsHarnessPayload(SkillBody, out var label);

        await Assert.That(harness).IsTrue();
        await Assert.That(label).IsEqualTo("Loaded skill: using-git-worktrees");
    }

    [Test]
    public async Task A_header_with_no_directory_still_reads_as_a_skill() {
        await Assert.That(SkillLoadParser.DisplayLabel("Base directory for this skill:\n\nbody")).IsEqualTo("Loaded a skill");
    }

    /// <summary>Someone asking about the header has words of their own before it, and stays an ask.</summary>
    [Test]
    public async Task A_message_quoting_the_header_is_not_a_match() {
        await Assert.That(UserPromptDisplay.IsHarnessPayload("what does this mean? " + SkillBody, out _)).IsFalse();
    }
}
