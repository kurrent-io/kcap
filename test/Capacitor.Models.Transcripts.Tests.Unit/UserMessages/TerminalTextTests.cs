using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.UserMessages;

public class TerminalTextTests {
    static string Esc => ((char)0x1b).ToString();

    [Test]
    public async Task Clean_StripsAnsiColorCodes() {
        var r = TerminalText.Clean($"{Esc}[32mLogin successful{Esc}[0m");
        await Assert.That(r).IsEqualTo("Login successful");
    }

    [Test]
    public async Task Clean_StripsC0ControlChars_KeepsTabAndNewline() {
        var r = TerminalText.Clean($"a{(char)0x07}b{(char)0x00}c\td\ne");
        await Assert.That(r).IsEqualTo("abc\td\ne");
    }

    [Test]
    public async Task Clean_DropsReplacementCharRuns() {
        var r = TerminalText.Clean("Welcome��� to Capacitor");
        await Assert.That(r).DoesNotContain("�");
        await Assert.That(r).Contains("Welcome");
        await Assert.That(r).Contains("to Capacitor");
    }

    [Test]
    public async Task FirstMeaningfulLine_SkipsBlankAndGarbageLines() {
        var r = TerminalText.FirstMeaningfulLine("��\n   \nWelcome to Capacitor\nStep 2");
        await Assert.That(r).IsEqualTo("Welcome to Capacitor");
    }

    [Test]
    public async Task FirstMeaningfulLine_CollapsesIntraLineWhitespace() {
        var r = TerminalText.FirstMeaningfulLine("Welcome   to    Capacitor");
        await Assert.That(r).IsEqualTo("Welcome to Capacitor");
    }

    [Test]
    public async Task FirstMeaningfulLine_AllGarbage_ReturnsNull() {
        await Assert.That(TerminalText.FirstMeaningfulLine(new string('�', 5))).IsNull();
    }

    [Test]
    public async Task Clean_StripsTerminatedOscSequence() {
        // OSC set-title: ESC ] 0 ; title BEL
        var r = TerminalText.Clean($"{Esc}]0;window title{(char)0x07}Welcome");
        await Assert.That(r).IsEqualTo("Welcome");
    }

    [Test]
    public async Task Clean_StripsTruncatedOscSequence() {
        // OSC cut off before its BEL/ST terminator — must not leak "]0;partial title".
        var r = TerminalText.Clean($"{Esc}]0;partial title");
        await Assert.That(r).IsEqualTo("");
    }

    [Test]
    public async Task Clean_StripsTruncatedCsiSequence() {
        // CSI cut off before its final byte — must not leak "[32".
        var r = TerminalText.Clean($"Welcome{Esc}[32");
        await Assert.That(r).IsEqualTo("Welcome");
    }

    [Test]
    public async Task Clean_PreservesInternalLineIndentation() {
        // Column spacing WITHIN the block (per-line indentation) is preserved — only the
        // outer ends of the whole block are trimmed.
        var r = TerminalText.Clean("header\n    indented row\n    aligned row");
        await Assert.That(r).IsEqualTo("header\n    indented row\n    aligned row");
    }
}
