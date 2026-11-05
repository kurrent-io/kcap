using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.UserMessages;

public class SystemInstructionParserTests {
    [Test]
    public async Task Parse_removes_every_block_and_names_each() {
        var parsed = SystemInstructionParser.Parse("<system-reminder>\na\n</system-reminder>\nFix it\n<system_reminder>b</system_reminder>");

        await Assert.That(parsed.CleanText).IsEqualTo("Fix it");
        await Assert.That(parsed.Blocks.Select(b => b.TagName)).IsEquivalentTo(new[] { "system-reminder", "system_reminder" });
    }

    [Test]
    public async Task Text_without_a_block_comes_back_as_it_is() {
        const string text = "  Fix it  ";

        await Assert.That(SystemInstructionParser.Parse(text).CleanText).IsSameReferenceAs(text);
        await Assert.That(SystemInstructionParser.StripLeading(text)).IsEqualTo("Fix it  ");
    }

    /// Only the blocks that open the text come off, so a block quoted further on stays where it was.
    [Test]
    public async Task StripLeading_removes_only_the_opening_blocks() {
        const string text = "<system-reminder>a</system-reminder>\n<SYSTEM_INSTRUCTIONS>b</SYSTEM_INSTRUCTIONS>\n<task-notification>\n<system-reminder>c</system-reminder>";

        await Assert.That(SystemInstructionParser.StripLeading(text)).IsEqualTo("<task-notification>\n<system-reminder>c</system-reminder>");
        await Assert.That(SystemInstructionParser.StripLeading("<system-reminder>unclosed")).IsEqualTo("<system-reminder>unclosed");
    }
}
