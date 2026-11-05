using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

public class ChatLiteralInputTests {
    static ChatProjectionResult Claude(string text, string? origin = null, bool meta = false, bool sidechain = false) {
        var chat = TranscriptChat.For("claude")!;
        var line = JsonSerializer.Serialize(new { type = "user", isMeta = meta, isSidechain = sidechain, origin = new { kind = origin }, message = new { content = text } });
        return chat.ProjectWithInputs(line, 1, DateTimeOffset.UnixEpoch, chat.CreateContext("s", null));
    }

    [Test]
    [Arguments("<system-reminder>literal</system-reminder>\nKeep this.")]
    [Arguments("```xml\n<command-name>/review</command-name><command-args>path</command-args>\n```")]
    [Arguments("<bash-input>echo example</bash-input>")]
    [Arguments("<agent-message from=\"example\">\nLiteral schema\n</agent-message>")]
    public async Task A_proven_literal_restores_one_original_user_receipt(string text) {
        var filtered = Claude(text);
        var literal = filtered.RestoreSubmittedInputs(candidate => candidate == text);
        await Assert.That(literal.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(literal.Envelopes.Single().Text).IsEqualTo(text);
        await Assert.That(literal.SubmittedInputs).IsEquivalentTo(new[] { text });
        await Assert.That(literal.Runs).IsEmpty();
        await Assert.That(filtered.RestoreSubmittedInputs(_ => false)).IsSameReferenceAs(filtered);
    }

    [Test]
    [Arguments(true, false, null)]
    [Arguments(false, true, null)]
    [Arguments(false, false, "peer")]
    [Arguments(false, false, "task-notification")]
    public async Task Trusted_injection_metadata_cannot_be_restored_by_text(bool meta, bool sidechain, string? origin) {
        var filtered = Claude("<task-notification>\n<task-id>t</task-id><status>completed</status>\n</task-notification>", origin, meta, sidechain);
        var literal = filtered.RestoreSubmittedInputs(_ => true);
        await Assert.That(literal).IsSameReferenceAs(filtered);
        await Assert.That(literal.SubmittedInputs).IsEmpty();
    }

    [Test]
    public async Task A_live_paste_body_takes_precedence_over_an_older_literal_wrapper() {
        const string body = "path /tmp/demo\necho hello";
        const string wrapper = "<pasted_content id=\"example\">\r\n" + body + "\r\n</pasted_content id=\"example\">";
        var literal = Claude(wrapper).RestoreSubmittedInputs(text => text == body, text => text == wrapper);
        await Assert.That(literal.SubmittedInputs).IsEquivalentTo(new[] { body });
        await Assert.That(literal.Envelopes.Single().Text).IsEqualTo(body);
    }

    /// The trimmed match stands in for the app's queue, which compares sends trimmed: the
    /// restored input must still be the body alone, whitespace inside it included, or ↑ recall
    /// resends the newlines around the wrapper.
    [Test]
    [Arguments("this is a test\none\ntwo")]
    [Arguments("\n    indented first line\nlast line with trailing spaces   ")]
    public async Task A_paste_restores_without_the_newlines_around_its_wrapper(string body) {
        var raw = "\n\n<pasted_content id=\"7ce1\">\n" + body + "\n</pasted_content id=\"7ce1\">\n";
        var literal = Claude(raw).RestoreSubmittedInputs(text => text.Trim() == body.Trim());
        await Assert.That(literal.SubmittedInputs).IsEquivalentTo(new[] { body });
        await Assert.That(literal.Envelopes.Single().Text).IsEqualTo(body);
    }

    /// Newlines at a message boundary held by typed text, not a wrapper, are the user's.
    [Test]
    public async Task Newlines_beside_typed_text_survive_a_restored_paste() {
        const string raw = "\nintro\n<pasted_content id=\"7ce1\">\nbody\n</pasted_content id=\"7ce1\">\noutro\n\n";
        const string expected = "\nintro\nbody\noutro\n\n";
        var literal = Claude(raw).RestoreSubmittedInputs(text => text.Trim() == expected.Trim());
        await Assert.That(literal.SubmittedInputs).IsEquivalentTo(new[] { expected });
    }

    [Test]
    public async Task A_live_slash_command_takes_precedence_over_an_older_literal_wrapper() {
        const string wrapper = "<command-name>/review</command-name><command-args>path</command-args>";
        var projected = Claude(wrapper);
        var restored = projected.RestoreSubmittedInputs(text => text == "/review path", text => text == wrapper);
        await Assert.That(restored).IsSameReferenceAs(projected);
        await Assert.That(restored.SubmittedInputs).IsEquivalentTo(new[] { "/review path" });
    }

    [Test]
    public async Task Combining_events_preserves_unrelated_rows_and_run_signals() {
        const string text = "<system-reminder>literal</system-reminder>";
        var run = new RunSignal.Started("t", "explore", "look", DateTimeOffset.UnixEpoch);
        var combined = ChatProjectionResult.Combine([
            Claude(text),
            new([new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "response")], [], [run]),
        ]);
        var restored = combined.RestoreSubmittedInputs(candidate => candidate == text);
        await Assert.That(restored.Envelopes.Select(e => e.Text!)).IsEquivalentTo(new[] { text, "response" });
        await Assert.That(restored.SubmittedInputs).IsEquivalentTo(new[] { text });
        await Assert.That(restored.Runs).IsEquivalentTo(new[] { run });
    }
}
