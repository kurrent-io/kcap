using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

// A tooltip from an inactive window can cover the foreground application.
public static class ToolTipFocusGate {
    static bool _installed;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(OnOpening);
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>(OnActiveChanged);
    }

    static void OnOpening(Control control, CancelRoutedEventArgs e) {
        var owner = TopLevel.GetTopLevel(control);
        // A native dropdown does not take activation from the window that owns it.
        while (owner is PopupRoot popup) owner = popup.ParentTopLevel;
        if (owner is WindowBase { IsActive: false }) e.Cancel = true;
    }

    static void OnActiveChanged(WindowBase window, AvaloniaPropertyChangedEventArgs e) {
        if (window.IsActive) return;
        // Closing an overlay tooltip removes visuals from this window.
        var open = window.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).ToArray();
        foreach (var control in open) ToolTip.SetIsOpen(control, false);
    }
}
