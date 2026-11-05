using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.UserMessages;

/// <summary>
/// Pins the one predicate that decides a user message is the harness talking rather than a
/// person, and the label each arm collapses to.
/// </summary>
public class UserPromptDisplayTests {
    [Test]
    public async Task A_task_notification_is_a_payload_and_labels_as_its_summary() {
        const string text = """
                            <task-notification>
                            <task-id>bg_9a21</task-id>
                            <tool-use-id>toolu_01Rf2</tool-use-id>
                            <status>completed</status>
                            <summary>Agent "Comment guidance check" finished</summary>
                            </task-notification>
                            """;

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out var label)).IsTrue();
        await Assert.That(label).IsEqualTo("✓ Agent \"Comment guidance check\" finished");
    }

    [Test]
    public async Task A_pure_local_command_payload_is_a_payload() {
        const string text = "<command-name>/clear</command-name><command-message>clear</command-message>";

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out var label)).IsTrue();
        await Assert.That(label).Contains("/clear");
    }

    [Test]
    public async Task A_real_ask_is_not_a_payload() {
        await Assert.That(UserPromptDisplay.IsHarnessPayload("fix the parser", out var label)).IsFalse();
        await Assert.That(label).IsEqualTo("");
    }

    /// <summary>
    /// The whole point of the purity check: a person writing ABOUT one of these tags still has
    /// text of their own, and showing the label instead of their ask would be the same failure.
    /// </summary>
    [Test]
    public async Task An_ask_that_merely_quotes_a_tag_is_not_a_payload() {
        const string text = "why does <bash-input>npm run build</bash-input> show up as a turn?";

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out _)).IsFalse();
    }

    /// <summary>
    /// Suppressing a payload discards what the person wrote, so a tag that merely STARTS the same
    /// way must not qualify — the check is on the tag's end, not its prefix.
    /// </summary>
    [Test]
    public async Task A_longer_tag_that_starts_the_same_way_is_not_a_payload() {
        const string text = "<task-notification-guide>how do these work?</task-notification-guide>";

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out _)).IsFalse();
    }

    /// <summary>A person quoting a notification and then asking about it still asked something.</summary>
    [Test]
    public async Task A_notification_quoted_before_a_question_is_not_a_payload() {
        const string text = """
                            <task-notification>
                            <status>completed</status>
                            <summary>Agent "Explore" finished</summary>
                            </task-notification>

                            why did this open a turn of its own?
                            """;

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out _)).IsFalse();
    }

    /// <summary>
    /// A payload cut before its closing tag is still wholly a payload — the parser tolerates the
    /// truncation, so the strictness must not turn that into a lost ask either way.
    /// </summary>
    [Test]
    public async Task A_truncated_notification_is_still_a_payload() {
        const string text = "<task-notification>\n<status>completed</status>\n<summary>Agent \"Explore\" fini";

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out _)).IsTrue();
    }

    [Test]
    [Arguments("[Request interrupted by user]")]
    [Arguments("[Request interrupted by user for tool use]")]
    [Arguments("  [Request interrupted by user]\n")]
    public async Task An_interrupt_marker_is_a_payload(string text) {
        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out var label)).IsTrue();
        await Assert.That(label).IsEqualTo("Interrupted");
    }

    /// <summary>Only the whole message is a marker: quoting one, or writing past it, is a real ask.</summary>
    [Test]
    [Arguments("why did I get [Request interrupted by user]?")]
    [Arguments("[Request interrupted by user] keep going but skip the tests")]
    [Arguments("[request interrupted by user]")]
    public async Task Text_around_an_interrupt_marker_is_not_a_payload(string text) {
        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out _)).IsFalse();
    }

    [Test]
    public async Task Blank_text_is_not_a_payload() {
        await Assert.That(UserPromptDisplay.IsHarnessPayload("   ", out _)).IsFalse();
        await Assert.That(UserPromptDisplay.IsHarnessPayload(null, out _)).IsFalse();
    }

    /// <summary>
    /// A payload wrapped in the harness's system blocks is still wholly a payload — the blocks
    /// come off before the purity check, or the wrapper would make it read as a person's ask.
    /// </summary>
    [Test]
    public async Task System_blocks_do_not_stop_a_payload_being_pure() {
        const string text = "<bash-input>ls</bash-input>\n<system-reminder>be careful</system-reminder>";

        await Assert.That(UserPromptDisplay.IsHarnessPayload(text, out var label)).IsTrue();
        await Assert.That(label).IsEqualTo("$ ls");
    }
}
