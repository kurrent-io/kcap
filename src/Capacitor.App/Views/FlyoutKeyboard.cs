using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Capacitor.App.Views;

/// A pointer-opened flyout leaves focus on its anchor, outside the popup, where Escape never reaches it.
public static class FlyoutKeyboard {
    public static void FocusFirstItem(Control content, NavigationMethod method) =>
        content.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled)?.Focus(method);

    public static void DismissOnEscape(FlyoutBase flyout, Control anchor, KeyEventArgs e, NavigationMethod refocus) {
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        flyout.Hide();
        anchor.Focus(refocus);
    }
}
