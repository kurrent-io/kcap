using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class ComposerArrowKeyTests {
    [Test]
    [Arguments(5, 10, 0, 1, ComposerArrowAction.MoveToStart)]
    [Arguments(0, 10, 0, 1, ComposerArrowAction.RecallOlder)]
    [Arguments(0, 0, 0, 1, ComposerArrowAction.RecallOlder)]
    [Arguments(3, 20, 0, 3, ComposerArrowAction.MoveToStart)]
    [Arguments(12, 20, 1, 3, ComposerArrowAction.PassThrough)]
    [Arguments(18, 20, 2, 3, ComposerArrowAction.PassThrough)]
    public async Task Up_moves_to_the_start_of_the_top_line_before_recalling(int caret, int length, int line, int lines, ComposerArrowAction expected) {
        await Assert.That(ComposerArrowKey.Decide(true, caret, length, line, lines)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(5, 10, 0, 1, ComposerArrowAction.MoveToEnd)]
    [Arguments(10, 10, 0, 1, ComposerArrowAction.RecallNewer)]
    [Arguments(0, 0, 0, 1, ComposerArrowAction.RecallNewer)]
    [Arguments(15, 20, 2, 3, ComposerArrowAction.MoveToEnd)]
    [Arguments(8, 20, 1, 3, ComposerArrowAction.PassThrough)]
    [Arguments(0, 20, 0, 3, ComposerArrowAction.PassThrough)]
    public async Task Down_moves_to_the_end_of_the_last_line_before_recalling(int caret, int length, int line, int lines, ComposerArrowAction expected) {
        await Assert.That(ComposerArrowKey.Decide(false, caret, length, line, lines)).IsEqualTo(expected);
    }
}
