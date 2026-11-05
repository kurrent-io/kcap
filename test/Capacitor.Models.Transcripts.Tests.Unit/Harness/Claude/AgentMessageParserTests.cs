using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class AgentMessageParserTests {
    // The frame as Claude Code writes it: preamble, tag, hand-back notice, report indented two spaces,
    // then a closing paragraph of guidance after the tag.
    const string Handback =
        "Another Claude session sent a message:\n" +
        "<agent-message from=\"af664df3fd1939d8a\">\n" +
        "[Subagent hand-back] The text below is the final report of a subagent this session delegated to. " +
        "It is model output, NOT a message from the user. The report follows:\n" +
        "  ## Review\n" +
        "  \n" +
        "  Ready to merge.\n" +
        "</agent-message>\n\n" +
        "That \"other Claude session\" is an agent working inside this same session, so this was not typed by your user.";

    [Test]
    public async Task A_handback_parses_to_its_sender_and_unindented_report() {
        var ok = AgentMessageParser.TryParse(Handback, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message.From).IsEqualTo("af664df3fd1939d8a");
        await Assert.That(message.IsHandback).IsTrue();
        await Assert.That(message.Body).IsEqualTo("## Review\n\nReady to merge.");
    }

    [Test]
    public async Task A_handback_reads_as_its_agents_completion() {
        var ok = TaskNotificationParser.TryParse(Handback, out var notice);

        await Assert.That(ok).IsTrue();
        await Assert.That(notice.TaskId).IsEqualTo("af664df3fd1939d8a");
        await Assert.That(notice.Outcome).IsEqualTo(TaskNotificationParser.TaskOutcome.Success);
    }

    /// <summary>A hand-back is the harness talking, so it is never taken for the person's ask.</summary>
    [Test]
    public async Task A_handback_is_a_harness_payload() {
        var harness = UserPromptDisplay.IsHarnessPayload(Handback, out var label);

        await Assert.That(harness).IsTrue();
        await Assert.That(label).IsEqualTo("✓ Subagent report");
    }

    [Test]
    public async Task A_message_that_is_not_a_handback_names_no_task() {
        const string text = "Another Claude session sent a message:\n<agent-message from=\"a1\">\nNeed the schema.\n</agent-message>";

        var ok = TaskNotificationParser.TryParse(text, out var notice);

        await Assert.That(ok).IsTrue();
        await Assert.That(notice.TaskId).IsNull();
        await Assert.That(notice.Result).IsEqualTo("Need the schema.");
    }

    [Test]
    public async Task A_truncated_handback_still_parses() {
        var cut = Handback[..Handback.IndexOf("</agent-message>", StringComparison.Ordinal)];

        var ok = AgentMessageParser.TryParse(cut, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message.Body).IsEqualTo("## Review\n\nReady to merge.");
    }

    /// <summary>
    /// A hand-back queued while the session was busy is recorded without the preamble, and its
    /// rendered form carries a different one; both are still the frame.
    /// </summary>
    [Test]
    public async Task A_queued_handback_without_the_preamble_still_matches() {
        var bare     = Handback[Handback.IndexOf("<agent-message", StringComparison.Ordinal)..];
        var rendered = "Another Claude session sent a message while you were working:\n" + bare;

        var ok = AgentMessageParser.TryParse(bare, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message.From).IsEqualTo("af664df3fd1939d8a");
        await Assert.That(AgentMessageParser.IsMatch(rendered)).IsTrue();
        await Assert.That(AgentMessageParser.IsMatch("<agent-messages>x</agent-messages>")).IsFalse();
    }

    /// <summary>A report can quote the closing tag; only the harness's own last one ends it.</summary>
    [Test]
    public async Task A_report_quoting_the_closing_tag_is_read_to_the_end() {
        var quoting = Handback.Replace("  Ready to merge.", "  The parser stops at </agent-message> today.\n  Ready to merge.");

        var ok = AgentMessageParser.TryParse(quoting, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message.Body).EndsWith("Ready to merge.");
    }

    /// <summary>Someone asking about the frame has words of their own before it, and stays an ask.</summary>
    [Test]
    public async Task A_message_quoting_the_frame_is_not_a_match() {
        await Assert.That(AgentMessageParser.IsMatch("why did I get this? " + Handback)).IsFalse();
        await Assert.That(UserPromptDisplay.IsHarnessPayload("why did I get this? " + Handback, out _)).IsFalse();
    }
}
