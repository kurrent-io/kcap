using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class TaskNotificationParserTests {
    const string BackgroundCommand =
        "<task-notification> <task-id>b2e87j2ps</task-id> <tool-use-id>toolu_01</tool-use-id> " +
        "<output-file>/tmp/tasks/b2e87j2ps.output</output-file> <status>completed</status> " +
        "<summary>Background command \"Wait for CI completion\" completed (exit code 0)</summary> </task-notification>";

    const string SubagentResult =
        "<task-notification> <task-id>a7f9</task-id> <tool-use-id>toolu_02</tool-use-id> " +
        "<output-file>/tmp/tasks/a7f9.output</output-file> <status>completed</status> " +
        "<summary>Agent \"Find rendering\" finished</summary> <note>heads up</note> " +
        "<result>The full agent output.</result> </task-notification>";

    [Test]
    public async Task IsMatch_TagLeading_True() {
        await Assert.That(TaskNotificationParser.IsMatch("  <task-notification> x")).IsTrue();
    }

    [Test]
    public async Task Parse_TruncatedResult_RecoversPriorFieldsAndPartialResult() {
        // Storage-truncated mid-<result> (no closing tag) — the case LocalCommandParser already tolerates.
        const string t = "<task-notification> <status>completed</status> " +
                         "<summary>Agent \"x\" finished</summary> <result>partial output that was cut o";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Status).IsEqualTo("completed");
        await Assert.That(n.Summary).IsEqualTo("Agent \"x\" finished");
        await Assert.That(n.Result).IsEqualTo("partial output that was cut o");
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Success);
    }

    [Test]
    public async Task Parse_XmlEscapedSummary_ReadsAsTheCommandWasTyped() {
        const string text = "<task-notification>\n<status>completed</status>\n"
                          + "<summary>Background command \"cd x &amp;&amp; ls 2&gt;/dev/null &lt;&lt;EOF\" completed (exit code 0)</summary>\n"
                          + "</task-notification>";

        await Assert.That(TaskNotificationParser.TryParse(text, out var n)).IsTrue();
        await Assert.That(n.Summary).IsEqualTo("Background command \"cd x && ls 2>/dev/null <<EOF\" completed (exit code 0)");
    }

    [Test]
    public async Task Parse_TruncatedSummary_RecoversPartialSummary() {
        // Cut off mid-<summary>, no closing tag and no later fields. Prior complete fields still parse.
        const string t = "<task-notification> <status>failed</status> " +
                         "<summary>Background command \"build\" failed (exit code 1";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Status).IsEqualTo("failed");
        await Assert.That(n.Summary).IsEqualTo("Background command \"build\" failed (exit code 1");
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Failure);
    }

    [Test]
    public async Task IsMatch_QuotingMidSentence_False() {
        await Assert.That(TaskNotificationParser.IsMatch("why is <task-notification> here?")).IsFalse();
    }

    [Test]
    public async Task IsMatch_Null_False() {
        await Assert.That(TaskNotificationParser.IsMatch(null)).IsFalse();
    }

    [Test]
    public async Task Parse_BackgroundCommand_ExtractsFieldsAndSuccess() {
        var ok = TaskNotificationParser.TryParse(BackgroundCommand, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.TaskId).IsEqualTo("b2e87j2ps");
        await Assert.That(n.ToolUseId).IsEqualTo("toolu_01");
        await Assert.That(n.OutputFile).IsEqualTo("/tmp/tasks/b2e87j2ps.output");
        await Assert.That(n.Status).IsEqualTo("completed");
        await Assert.That(n.Summary).IsEqualTo("Background command \"Wait for CI completion\" completed (exit code 0)");
        await Assert.That(n.ExitCode).IsEqualTo(0);
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Success);
    }

    [Test]
    public async Task Parse_SubagentResult_CapturesNoteAndResult() {
        var ok = TaskNotificationParser.TryParse(SubagentResult, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Note).IsEqualTo("heads up");
        await Assert.That(n.Result).IsEqualTo("The full agent output.");
        await Assert.That(n.ExitCode).IsNull();
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Success);
    }

    [Test]
    public async Task Parse_NonZeroExit_Failure() {
        const string t = "<task-notification> <status>completed</status> " +
                         "<summary>Background command \"build\" failed (exit code 1)</summary> </task-notification>";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.ExitCode).IsEqualTo(1);
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Failure);
    }

    [Test]
    [Arguments("failed")]
    [Arguments("error")]
    [Arguments("cancelled")]
    [Arguments("canceled")]
    [Arguments("aborted")]
    [Arguments("timed_out")]
    [Arguments("timeout")]
    [Arguments("interrupted")]
    [Arguments("killed")]
    public async Task Parse_FailureStatus_Failure(string status) {
        var t = $"<task-notification> <status>{status}</status> <summary>x</summary> </task-notification>";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Failure);
    }

    [Test]
    public async Task Parse_UnknownStatus_ExitZeroOrAbsent_Unknown() {
        const string t = "<task-notification> <status>running</status> <summary>x</summary> </task-notification>";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Unknown);
    }

    [Test]
    public async Task TryParse_EmptyTagLeading_TrueUnknownAllNull() {
        var ok = TaskNotificationParser.TryParse("<task-notification></task-notification>", out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(n.Summary).IsNull();
        await Assert.That(n.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Unknown);
    }

    [Test]
    public async Task DisplayLabel_UsesRawSummaryVerbatim_NoDuplicateExit() {
        var ok = TaskNotificationParser.TryParse(BackgroundCommand, out var n);

        await Assert.That(ok).IsTrue();
        var label = TaskNotificationParser.DisplayLabel(n);

        await Assert.That(label).IsEqualTo("✓ Background command \"Wait for CI completion\" completed (exit code 0)");
    }

    [Test]
    public async Task DisplayLabel_FailureGlyphAndGenericBody_WhenSummaryAbsent() {
        const string t = "<task-notification> <status>failed</status> </task-notification>";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        await Assert.That(TaskNotificationParser.DisplayLabel(n)).IsEqualTo("✗ Background task failed");
    }

    [Test]
    public async Task DisplayLabel_CappedAt200Chars() {
        var big = new string('x', 500);
        var t   = $"<task-notification> <status>completed</status> <summary>{big}</summary> </task-notification>";

        var ok = TaskNotificationParser.TryParse(t, out var n);

        await Assert.That(ok).IsTrue();
        var label = TaskNotificationParser.DisplayLabel(n);

        await Assert.That(label.Length).IsLessThanOrEqualTo(202); // glyph(2) + 200 body, or 200+…
        await Assert.That(label.EndsWith('…')).IsTrue();
    }

    [Test]
    public async Task IsShellTask_TellsCommandsAndMonitorsFromAnAgent() {
        const string monitor = "<task-notification> <task-id>bmon1</task-id> <tool-use-id>toolu_03</tool-use-id> " +
                               "<status>completed</status> <summary>Monitor \"server log\" stream ended</summary> </task-notification>";

        _ = TaskNotificationParser.TryParse(BackgroundCommand, out var command);
        _ = TaskNotificationParser.TryParse(monitor, out var watched);
        _ = TaskNotificationParser.TryParse(SubagentResult, out var agent);

        await Assert.That(TaskNotificationParser.IsShellTask(command)).IsTrue();
        await Assert.That(TaskNotificationParser.IsShellTask(watched)).IsTrue();
        await Assert.That(TaskNotificationParser.IsShellTask(agent)).IsFalse();
    }
}
