using Capacitor.App.Services;
using XTerm.Buffer;
using static Capacitor.App.Tests.Unit.Ansi;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class XtermTerminalSurfaceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static AttributeData Attributes(XtermTerminalSurface surface, int x) =>
        surface.Model.Terminal.Engine.Buffer.GetLine(0)![x].Attributes;

    static bool Underlined(XtermTerminalSurface surface, int x) => Attributes(surface, x).IsUnderline();

    /// Pins that an underline-colour selector's arguments are not read as attribute codes: it
    /// underlines nothing, while a real underline still does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_underline_colour_in_the_feed_does_not_underline_later_cells() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            surface.Feed(Esc + "[58;2;255;4;4ma" + Esc + "[39mb" + Esc + "[4mc");

            await Assert.That(Underlined(surface, 0)).IsFalse();
            await Assert.That(Underlined(surface, 1)).IsFalse();
            await Assert.That(Underlined(surface, 2)).IsTrue();
        });
    }

    /// Pins that the keyboard-mode sequences Claude Code sends on every return to raw mode,
    /// which share SGR's final byte, style nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_keyboard_mode_set_in_the_feed_does_not_style_later_cells() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            surface.Feed(Esc + "[<u" + Esc + "[>5u" + Esc + "[>4;2ma");

            await Assert.That(Attributes(surface, 0)).IsEqualTo(AttributeData.Default);
        });
    }

    /// Pins that the pane does not advertise the kitty keyboard protocol its key handling does
    /// not encode: the query goes unanswered and a push sets no flags. The cursor position
    /// report in the same feed proves the reply path is live, so the silence is the engine's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_kitty_keyboard_protocol_is_not_advertised() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            var replies = new List<string>();
            surface.InputProduced += bytes => replies.Add(System.Text.Encoding.ASCII.GetString(bytes));

            surface.Feed(Esc + "[>5u" + Esc + "[?u" + Esc + "[6n");

            await Assert.That(replies).IsEquivalentTo([Esc + "[1;1R"]);
            await Assert.That(surface.Model.Terminal.Engine.KittyKeyboardActive).IsFalse();
        });
    }

    /// Pins the colon sub-parameter forms: 4:0 is underline off, and a colon truecolour selects
    /// the colour its semicolon form does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Colon_sub_parameters_read_as_their_semicolon_forms() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            surface.Feed(Esc + "[4ma" + Esc + "[4:0mb" + Esc + "[38:2::10:20:30mc" + Esc + "[0;38;2;10;20;30md");

            await Assert.That(Underlined(surface, 0)).IsTrue();
            await Assert.That(Underlined(surface, 1)).IsFalse();
            await Assert.That(Attributes(surface, 2)).IsEqualTo(Attributes(surface, 3));
            await Assert.That(Attributes(surface, 2)).IsNotEqualTo(AttributeData.Default);
        });
    }

    /// Pins the diagnostic tap: with a dump path, every fed frame is appended to that file as
    /// received, before the glyph substitution rewrites it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dump_path_receives_every_fed_frame_before_rewriting() {
        await RunOnUiAsync(async () => {
            var dump = Tmp.PathTo("feed.bin");
            var surface = new XtermTerminalSurface(40, 4, dump);
            surface.Feed("ab⏵");
            surface.Feed("c");

            await Assert.That(File.ReadAllText(dump)).IsEqualTo("ab⏵c");
            await Assert.That(surface.Model.Terminal.Engine.GetLine(0)).IsEqualTo("ab►c");
        });
    }

    /// Pins that a feed parsed in pieces reads as the same text: marks on either side of a parse
    /// seam are both rewritten, and a surrogate pair split by one draws as the character.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("⏵⏵", "►►")]
    [Arguments("𝄞z", "𝄞z")]
    public async Task A_feed_split_across_parse_seams_draws_as_one(string tail, string drawn) {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            // Carriage returns keep the cursor on line 0 while pushing the tail onto the seam.
            surface.Feed([new string('\r', 32 * 1024 - 1) + tail]);

            await Assert.That(surface.Model.Terminal.Engine.GetLine(0)).IsEqualTo(drawn);
        });
    }

    /// Pins that pieces fed together reach the emulator in order, each through the tap and the rewrite.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pieces_fed_together_draw_in_order() {
        await RunOnUiAsync(async () => {
            var dump = Tmp.PathTo("feed.bin");
            var surface = new XtermTerminalSurface(40, 4, dump);
            surface.Feed(["ab", "⏵c"]);

            await Assert.That(File.ReadAllText(dump)).IsEqualTo("ab⏵c");
            await Assert.That(surface.Model.Terminal.Engine.GetLine(0)).IsEqualTo("ab►c");
        });
    }

    /// Pins the sidecar a capture is read against: each line carries the dump's byte length at that
    /// moment, so a size can be placed between the bytes fed before and after it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dump_path_stamps_each_event_with_the_dump_length_so_far() {
        await RunOnUiAsync(async () => {
            var dump = Tmp.PathTo("feed.bin");
            var surface = new XtermTerminalSurface(40, 4, dump);
            surface.Feed("ab⏵");
            surface.NoteDiagnostic("resize-sent 50x10");

            var events = File.ReadAllLines(dump + ".events");
            await Assert.That(events[0]).EndsWith("\t@0\tsurface 40x4 reflow=False placeholder=False");
            await Assert.That(events[^1]).EndsWith("\t@5\tresize-sent 50x10");
        });
    }

    /// Pins that a pane-sized surface counts as measured once its size leaves the placeholder, even
    /// when nothing raised a size event.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_placeholder_surface_is_measured_once_resized() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(80, 24, awaitsPaneSize: true);
            await Assert.That(surface.Measured).IsFalse();

            surface.Resize(120, 40);

            await Assert.That(surface.Measured).IsTrue();
        });
    }

    /// Pins that the surface rewraps a soft-wrapped row when it widens: with the resize quirk ConPTY
    /// leaves that to the terminal. The row is finished first; the engine does not rewrap the row the
    /// cursor is still on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reflowing_surface_rejoins_a_wrapped_row_when_it_widens() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(20, 4, reflowOnResize: true);
            surface.Feed(new string('x', 30) + "\r\nnext");

            surface.Resize(40, 4);

            await Assert.That(surface.Model.Terminal.Engine.GetLine(0)).IsEqualTo(new string('x', 30));
        });
    }

    /// Pins that a resized viewport leaves the cursor on the line it was on, so the repaint an
    /// agent sends on SIGWINCH lands on the tail rather than above it. The feed must overflow the
    /// viewport first — without scrollback the emulator has no lines to give back on a grow.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(10)]
    [Arguments(3)]
    public async Task A_resized_viewport_keeps_the_cursor_on_its_line(int rows) {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            for (var i = 1; i <= 12; i++) surface.Feed($"line {i}\r\n");
            surface.Feed("prompt");
            var buffer = surface.Model.Terminal.Buffer;
            var line = buffer.BaseY + buffer.Y;

            surface.Resize(40, rows);

            await Assert.That(buffer.BaseY + buffer.Y).IsEqualTo(line);
            await Assert.That(buffer.GetLine(line)!.TranslateToString(true)).IsEqualTo("prompt");
        });
    }
}
