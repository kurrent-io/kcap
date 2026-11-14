using Avalonia.Input;

namespace Capacitor.App.Views;

/// The keys that step back and forward through the main window's history.
public static class HistoryShortcuts {
    public static KeyGesture Back { get; } = ShortcutLabels.UsesCommandKey
        ? new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta)
        : new KeyGesture(Key.Left, KeyModifiers.Alt);

    public static KeyGesture Forward { get; } = ShortcutLabels.UsesCommandKey
        ? new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta)
        : new KeyGesture(Key.Right, KeyModifiers.Alt);

    /// ⌘← and ⌘→ step only when they have no caret to move: a focused text box holding text or an
    /// input method's uncommitted composition keeps them, and a focused terminal keeps them as its
    /// own keys. Focus is read only for a bare ⌘← or ⌘→, never on ordinary typing. Returns true for
    /// back, false for forward, null when the key stays with the control.
    public static bool? CaretArrowStep(Key key, KeyModifiers modifiers, Func<(bool TerminalFocused, string? Text, string? Preedit)> focus) {
        if (modifiers != KeyModifiers.Meta || key is not (Key.Left or Key.Right)) return null;
        var (terminalFocused, text, preedit) = focus();
        if (terminalFocused || !string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(preedit)) return null;
        return key == Key.Left;
    }
}
