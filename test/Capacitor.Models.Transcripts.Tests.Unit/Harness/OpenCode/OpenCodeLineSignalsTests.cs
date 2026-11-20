using Capacitor.Models.Transcripts.Harness.OpenCode;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.OpenCode;

/// <summary>What the server's raw OpenCode route reads beside a line's events, read on the client for the canonical
/// route: a root session's title, as a rename.</summary>
public class OpenCodeLineSignalsTests {
    static LineSignals Read(string line, string? agentId = null) => OpenCodeLineSignals.Instance.Read(line, "ses_1", agentId);

    [Test]
    public async Task A_title_line_is_a_rename_at_its_own_time() =>
        await Assert.That(Read("""{"type":"session_title","title":"  Fix   the build ","time":1795148348000}""").Title)
            .IsEqualTo(new TitleSighting("rename", "Fix the build", DateTimeOffset.FromUnixTimeMilliseconds(1795148348000), false));

    [Test]
    public async Task A_title_line_without_a_time_is_an_untimed_rename() =>
        await Assert.That(Read("""{"type":"session_title","title":"Fix the build"}""").Title)
            .IsEqualTo(new TitleSighting("rename", "Fix the build", null, false));

    [Test]
    [Arguments("""{"type":"session_title","title":"New session - 2026-11-19T10:00:00.000Z","time":1}""", null)]
    [Arguments("""{"type":"session_title","title":"   "}""", null)]
    [Arguments("""{"type":"session_title","title":"x"}""", "ses_child")]
    [Arguments("""{"id":"msg_u","type":"user","time":{"created":1},"text":"session_title"}""", null)]
    [Arguments("""{"info":{"role":"user"},"parts":[{"type":"text","text":"\"session_title\""}]}""", null)]
    [Arguments("""not json "session_title" """, null)]
    public async Task A_placeholder_a_subagents_line_or_a_message_carries_no_title(string line, string? agentId) =>
        await Assert.That(Read(line, agentId)).IsEqualTo(LineSignals.None);

    [Test]
    public async Task OpenCode_reads_its_own_signals() =>
        await Assert.That(TranscriptSignals.For("opencode")).IsSameReferenceAs(OpenCodeLineSignals.Instance);
}
