namespace Capacitor.Cli.Recording.Tests.Unit;

public class StripSystemInstructionsTests {
    [Test]
    [Arguments("Hello <system_instructions>secret stuff</system_instructions> world", "Hello  world")]
    [Arguments("<system-instructions>block</system-instructions>actual prompt", "actual prompt")]
    [Arguments("<system-reminder>reminder content</system-reminder>do the thing", "do the thing")]
    [Arguments("<system_reminder>stuff</system_reminder>real text", "real text")]
    [Arguments("<SYSTEM_INSTRUCTIONS>loud</SYSTEM_INSTRUCTIONS>quiet", "quiet")]
    public async Task Strips_KnownSystemTags(string input, string expected) {
        var result = TranscriptLineText.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task PreservesText_WithNoSystemTags() {
        var result = TranscriptLineText.StripSystemInstructions("just a normal prompt");
        await Assert.That(result).IsEqualTo("just a normal prompt");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions() {
        var result = TranscriptLineText.StripSystemInstructions("<system_instructions>everything is instructions</system_instructions>");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ReturnsNull_ForNullInput() {
        var result = TranscriptLineText.StripSystemInstructions(null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_MultipleBlocks() {
        const string input  = "<system_instructions>first</system_instructions>middle<system-reminder>second</system-reminder>end";
        var          result = TranscriptLineText.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("middleend");
    }

    [Test]
    public async Task Strips_MultilineContent() {
        const string input  = "<system_instructions>\nline1\nline2\nline3\n</system_instructions>actual request";
        var          result = TranscriptLineText.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("actual request");
    }

    [Test]
    public async Task CaseInsensitive_MixedCase() {
        var result = TranscriptLineText.StripSystemInstructions("<System_Instructions>stuff</System_Instructions>prompt");
        await Assert.That(result).IsEqualTo("prompt");
    }
}
