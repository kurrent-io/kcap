using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Views;

/// Handles presses on blank custom title-bar space. Buttons consume their own presses.
public static class WindowChrome {
    public static void BeginDrag(Visual host, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(host).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            TopLevel.GetTopLevel(host) is not Window window) return;

        if (e.ClickCount == 2) {
            e.Handled = true;
            if (!window.CanResize || window.WindowState == WindowState.FullScreen) return;

            var action = OperatingSystem.IsMacOS() ? MacWindowPreferences.ReadTitleBarDoubleClickAction() : null;
            window.WindowState = DoubleClickState(window.WindowState, action);
            return;
        }

        window.BeginMoveDrag(e);
    }

    internal static WindowState DoubleClickState(WindowState current, string? macAction) => macAction switch {
        "None" => current,
        "Minimize" => WindowState.Minimized,
        // Avalonia maps Maximized to native window zoom on macOS.
        _ => current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
    };
}
