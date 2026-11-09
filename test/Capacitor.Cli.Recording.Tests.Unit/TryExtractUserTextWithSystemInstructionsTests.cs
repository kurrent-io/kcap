namespace Capacitor.Cli.Recording.Tests.Unit;

public class TryExtractUserTextWithSystemInstructionsTests {
    [Test]
    public async Task Strips_SystemInstructions_FromStringContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>secret</system_instructions>fix the bug"}}""";
        var          result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions_InContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>only instructions here</system_instructions>"}}""";
        var          result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_SystemInstructions_FromArrayContent() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>reminder</system-reminder>do stuff"}]}}""";
        var          result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("do stuff");
    }
}
