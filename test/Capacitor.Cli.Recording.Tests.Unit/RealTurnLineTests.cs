namespace Capacitor.Cli.Recording.Tests.Unit;

public class RealTurnLineTests {
    /// <summary>The Local transcript's own lines reach the server once a turn is taken, and its session.start
    /// alone does not count as one.</summary>
    [Test]
    [Arguments("""{"type":"user.message","data":{"content":"Read README.md","attachments":[]},"id":"abdf2e64-7dbe-4ac0-8e8b-5974db55fb46","timestamp":"2026-11-05T03:39:56.346Z"}""", true)]
    [Arguments("""{"type":"session.start","data":{"sessionId":"d72e59eb-1ced-4194-8df4-c3ae346b63d7","version":1,"producer":"copilot-agent","copilotVersion":"0.73.0","vscodeVersion":"1.145.0"},"id":"24b4ba4b-0543-4afc-b3c5-aa128944879d","timestamp":"2026-11-05T03:39:56.150Z"}""", false)]
    public async Task A_Local_transcript_line_is_a_real_turn_only_when_it_is_one(string line, bool expected) {
        await Assert.That(TranscriptLineText.IsRealTurnLine(line, "copilot")).IsEqualTo(expected);
    }
}
