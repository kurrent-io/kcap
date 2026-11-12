using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// The one "empty chrome space drags the window" rule: the client area extends into the title
/// bar, so each surface's top strip must move the window as a system title bar would. Buttons
/// in a strip mark their presses handled, so a wired handler only fires on the blank stretch.
public static class WindowChrome {
    public static void BeginDrag(Visual host, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed || TopLevel.GetTopLevel(host) is not Window window) return;
        if (OperatingSystem.IsLinux() && e.ClickCount == 2 && window.CanMaximize) {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        window.BeginMoveDrag(e);
    }
}
