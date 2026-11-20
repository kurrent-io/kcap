using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class ClaudeLineSignalsTests {
    const string Session = "5cdc4fccff9e4fdc8d93cf47ff5a140a";

    static LineSignals Read(string line, string? agentId = null) => ClaudeLineSignals.Instance.Read(line, Session, agentId);

    [Test]
    public async Task A_custom_title_of_this_session_is_an_untimed_rename() =>
        await Assert.That(Read("""{"type":"custom-title","customTitle":"  My   rename ","sessionId":"5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a"}""").Title)
            .IsEqualTo(new TitleSighting("rename", "My rename", null, false));

    [Test]
    public async Task An_ai_title_is_an_auto_title() =>
        await Assert.That(Read("""{"type":"ai-title","aiTitle":"Fix it","sessionId":"5cdc4fccff9e4fdc8d93cf47ff5a140a"}""").Title)
            .IsEqualTo(new TitleSighting("auto", "Fix it", null, false));

    [Test]
    [Arguments("""{"type":"summary","summary":"Title","leafUuid":"u"}""")]
    [Arguments("""{"type": "summary", "summary": "Title"}""")]
    public async Task A_legacy_summary_is_a_legacy_auto_title(string line) =>
        await Assert.That(Read(line).Title).IsEqualTo(new TitleSighting("auto", "Title", null, true));

    [Test]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"11111111-2222-3333-4444-555555555555"}""")]
    [Arguments("""{"type":"custom-title","customTitle":"x","sessionId":"11111111-2222-3333-4444-555555555555"}""")]
    [Arguments("""{"type":"ai-title","aiTitle":"x"}""")]
    [Arguments("""{"type":"custom-title","customTitle":"   ","sessionId":"5cdc4fccff9e4fdc8d93cf47ff5a140a"}""")]
    [Arguments("""{"type":1,"customTitle":"x","sessionId":"s","note":"custom-title"}""")]
    [Arguments("""{"type":"user","message":{"content":"ai-title"}}""")]
    public async Task Lines_that_are_not_a_title_of_this_session_carry_none(string line) =>
        await Assert.That(Read(line).Title).IsNull();

    [Test]
    public async Task A_custom_title_named_ai_title_is_still_a_rename() =>
        await Assert.That(Read("""{"type":"custom-title","customTitle":"ai-title","sessionId":"5cdc4fccff9e4fdc8d93cf47ff5a140a"}""").Title)
            .IsEqualTo(new TitleSighting("rename", "ai-title", null, false));

    [Test]
    public async Task A_title_is_capped_at_200_characters() =>
        await Assert.That(Read($$"""{"type":"ai-title","aiTitle":"{{new string('x', 300)}}","sessionId":"5cdc4fccff9e4fdc8d93cf47ff5a140a"}""").Title!.Title.Length)
            .IsEqualTo(200);

    [Test]
    public async Task A_turn_duration_ends_a_turn_unless_sidechain() {
        await Assert.That(Read("""{"type":"system","subtype":"turn_duration"}""").EndsTurn).IsTrue();
        await Assert.That(Read("""{"type":"system","subtype":"turn_duration","isSidechain":true}""").EndsTurn).IsFalse();
        await Assert.That(Read("""{"type":"assistant","message":{"content":"turn_duration"}}""").EndsTurn).IsFalse();
    }

    [Test]
    public async Task An_assistant_record_gives_its_ids_type_and_timestamp_as_written() {
        var signals = Read("""{"type":"assistant","uuid":"u1","parentUuid":"p1","timestamp":"2026-09-29T10:00:00.000+02:00","message":{"timestamp":"2099-01-01T00:00:00Z"}}""");

        await Assert.That(signals).IsEqualTo(new LineSignals("u1", "p1", "assistant", "2026-09-29T10:00:00.000+02:00", null, false));
    }

    [Test]
    public async Task A_timestamp_that_is_not_a_string_reads_as_absent() =>
        await Assert.That(Read("""{"type":"user","timestamp":42}""").Timestamp).IsNull();

    [Test]
    public async Task A_subagent_transcript_yields_no_title_and_no_turn_end() {
        await Assert.That(Read("""{"type":"custom-title","customTitle":"x","sessionId":"5cdc4fccff9e4fdc8d93cf47ff5a140a"}""", "a1").Title).IsNull();
        await Assert.That(Read("""{"type":"system","subtype":"turn_duration"}""", "a1").EndsTurn).IsFalse();
    }

    [Test]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("")]
    public async Task A_line_that_is_not_a_json_object_yields_none(string line) =>
        await Assert.That(Read(line)).IsEqualTo(LineSignals.None);

    [Test]
    public async Task Only_claude_has_a_reader() {
        await Assert.That(TranscriptSignals.For("claude")).IsNotNull();
        await Assert.That(TranscriptSignals.For("codex")).IsNull();
    }
}
