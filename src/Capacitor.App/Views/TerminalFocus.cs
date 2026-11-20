using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SvcSystems.UI.Terminal;

namespace Capacitor.App.Views;

static class TerminalFocus {
    /// An asynchronous attach may take focus from its own tab button or notice.
    public static bool CanTakeFocus(TerminalControl terminal, Control tabButton, Control pane) =>
        TopLevel.GetTopLevel(terminal)?.FocusManager?.GetFocusedElement() is null
        || terminal.IsKeyboardFocusWithin || tabButton.IsKeyboardFocusWithin || pane.IsKeyboardFocusWithin;

    /// Read on each key press: focus is not a command input, so nothing may cache it.
    public static bool Has(TopLevel window) {
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null) {
            if (focused is TerminalControl) return true;
            focused = focused.GetVisualParent();
        }
        return false;
    }
}
