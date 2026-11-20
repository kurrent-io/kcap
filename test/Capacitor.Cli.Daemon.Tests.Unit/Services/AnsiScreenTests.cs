using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Pins the repaint sequences ConPTY sends on Windows. A sequence the screen ignores leaves the text it was
/// meant to remove, and a dialog read off that text outlives the dialog.
public class AnsiScreenTests {
    static string Draw(int cols, int rows, string bytes) {
        var screen = new AnsiScreen(cols, rows);
        screen.Write(Encoding.UTF8.GetBytes(bytes));
        return screen.Text();
    }

    [Test]
    [Arguments("hello world\e[1G\e[5X", "      world")]
    [Arguments("hello\e[1G\e[X", " ello")]
    [Arguments("abcdef\e[2G\e[2P", "adef")]
    [Arguments("abcdef\e[2G\e[2@", "a  bcdef")]
    [Arguments("abc\e[2G\e[9P", "a")]
    public async Task Characters_are_erased_deleted_and_inserted_at_the_cursor(string bytes, string expected) =>
        await Assert.That(Draw(20, 3, bytes)).IsEqualTo(expected);

    /// A full row leaves the cursor waiting to wrap; an edit there acts on the last cell, and the next
    /// character lands there instead of on the next row.
    [Test]
    [Arguments("abcde\e[XZ", "abcdZ")]
    [Arguments("abcde\e[PZ", "abcdZ")]
    [Arguments("abcde\e[@Z", "abcdZ")]
    public async Task An_edit_after_a_full_row_acts_on_its_last_cell(string bytes, string expected) =>
        await Assert.That(Draw(5, 3, bytes)).IsEqualTo(expected);

    [Test]
    [Arguments("\e[1;1H\e[M", "two\nthree")]
    [Arguments("\e[1;1H\e[L", "\none\ntwo\nthree")]
    [Arguments("\e[S", "two\nthree")]
    [Arguments("\e[T", "\none\ntwo\nthree")]
    [Arguments("\e[1;1H\eM", "\none\ntwo\nthree")]
    public async Task Lines_are_deleted_inserted_and_scrolled(string bytes, string expected) =>
        await Assert.That(Draw(20, 4, "one\r\ntwo\r\nthree" + bytes)).IsEqualTo(expected);

    /// Rows 2 and 3 are the region; a move up stops at its top from anywhere at or below it, and a move
    /// down at its bottom from anywhere at or above it.
    [Test]
    [Arguments("\e[2;3r\e[4;1H\e[9AX", "a\nX\nc\nd")]
    [Arguments("\e[2;3r\e[1;1H\e[9BX", "a\nb\nX\nd")]
    [Arguments("\e[2;3r\e[2;1H\e[5BX", "a\nb\nX\nd")]
    [Arguments("\e[2;3r\e[3;1H\e[5EX", "a\nb\nX\nd")]
    [Arguments("\e[2;3r\e[3;1H\e[5AX", "a\nX\nc\nd")]
    [Arguments("\e[2;3r\e[3;1H\e[5FX", "a\nX\nc\nd")]
    public async Task A_relative_move_stops_at_the_scroll_margins(string bytes, string expected) =>
        await Assert.That(Draw(20, 4, "a\r\nb\r\nc\r\nd" + bytes)).IsEqualTo(expected);

    [Test]
    public async Task A_line_feed_at_the_bottom_margin_scrolls_only_the_region() =>
        await Assert.That(Draw(20, 4, "a\r\nb\r\nc\r\nd\e[2;3r\e[3;1H\n")).IsEqualTo("a\nc\n\nd");

    [Test]
    [Arguments("\e[3dX", "\n\nX")]
    [Arguments("ab\e7\e[3;5Hx\e8c", "abc\n\n    x")]
    [Arguments("one\e[2E2\e[F1", "one\n1\n2")]
    public async Task The_cursor_moves_to_rows_and_saved_positions(string bytes, string expected) =>
        await Assert.That(Draw(20, 3, bytes)).IsEqualTo(expected);
}
