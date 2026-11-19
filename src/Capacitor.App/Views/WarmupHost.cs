using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// Holds a view that must be laid out and rendered but reached by no one: transparent, outside hit
/// testing and Tab order, and with no automation children, so an accessibility client cannot list or
/// focus what it holds.
internal sealed class WarmupHost : Decorator {
    public WarmupHost(Control child) {
        Child = child;
        Opacity = 0;
        IsHitTestVisible = false;
        Focusable = false;
        ZIndex = -1;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WarmupHostAutomationPeer(this);
}
