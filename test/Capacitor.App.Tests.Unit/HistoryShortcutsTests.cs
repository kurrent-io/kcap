using Avalonia.Input;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class HistoryShortcutsTests {
    [Test]
    [Arguments(Key.Left, true)]
    [Arguments(Key.Right, false)]
    public async Task Command_arrow_steps_when_nothing_holding_text_has_focus(Key key, bool back) {
        await Assert.That(HistoryShortcuts.CaretArrowStep(key, KeyModifiers.Meta, terminalFocused: false, focusedText: null)).IsEqualTo(back);
    }

    [Test]
    public async Task Command_arrow_steps_from_an_empty_text_box() {
        await Assert.That(HistoryShortcuts.CaretArrowStep(Key.Left, KeyModifiers.Meta, false, "")).IsTrue();
    }

    /// A text box holding text keeps ⌘← and ⌘→ as line start and end, whitespace included.
    [Test]
    [Arguments("draft")]
    [Arguments(" ")]
    public async Task Command_arrow_stays_with_a_text_box_that_holds_text(string text) {
        await Assert.That(HistoryShortcuts.CaretArrowStep(Key.Left, KeyModifiers.Meta, false, text)).IsNull();
        await Assert.That(HistoryShortcuts.CaretArrowStep(Key.Right, KeyModifiers.Meta, false, text)).IsNull();
    }

    [Test]
    public async Task Command_arrow_stays_with_a_focused_terminal() {
        await Assert.That(HistoryShortcuts.CaretArrowStep(Key.Left, KeyModifiers.Meta, terminalFocused: true, null)).IsNull();
    }

    /// Shift+⌘← selects to the line start and a bare arrow moves the caret; neither is a step.
    [Test]
    [Arguments(Key.Left, KeyModifiers.Meta | KeyModifiers.Shift)]
    [Arguments(Key.Left, KeyModifiers.None)]
    [Arguments(Key.Left, KeyModifiers.Alt)]
    [Arguments(Key.Up, KeyModifiers.Meta)]
    public async Task Only_a_bare_command_arrow_is_a_step(Key key, KeyModifiers modifiers) {
        await Assert.That(HistoryShortcuts.CaretArrowStep(key, modifiers, false, null)).IsNull();
    }

    [Test]
    public async Task Gestures_follow_the_platform() {
        var (back, forward) = ShortcutLabels.UsesCommandKey
            ? (new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta), new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta))
            : (new KeyGesture(Key.Left, KeyModifiers.Alt), new KeyGesture(Key.Right, KeyModifiers.Alt));

        await Assert.That(HistoryShortcuts.Back).IsEqualTo(back);
        await Assert.That(HistoryShortcuts.Forward).IsEqualTo(forward);
    }
}
