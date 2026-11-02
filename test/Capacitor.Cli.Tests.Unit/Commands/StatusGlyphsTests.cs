using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusGlyphsTests {
    const string Line = "Claude ✓  Codex ✗ — Settings → Hooks…";

    [Test]
    public async Task A_stream_that_cannot_carry_the_marks_gets_every_stand_in() {
        await Assert.That(StatusGlyphs.ForConsole(Line, encodable: false, windows: true))
            .IsEqualTo("Claude ok  Codex x - Settings -> Hooks...");
    }

    /// <summary>The Windows console fonts draw punctuation but not the check marks, which would leave a wired
    /// and an unwired harness as two identical boxes.</summary>
    [Test]
    public async Task A_windows_console_keeps_the_punctuation_and_spells_out_the_marks() {
        await Assert.That(StatusGlyphs.ForConsole(Line, encodable: true, windows: true))
            .IsEqualTo("Claude ok  Codex x — Settings → Hooks…");
    }

    [Test]
    public async Task Any_other_unicode_terminal_gets_the_text_as_it_is() {
        await Assert.That(StatusGlyphs.ForConsole(Line, encodable: true, windows: false)).IsEqualTo(Line);
    }
}
