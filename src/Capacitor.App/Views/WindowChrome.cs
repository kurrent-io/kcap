using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Views;

/// Handles presses on blank custom title-bar space. Buttons consume their own presses.
public static class WindowChrome {
    sealed class Presses { public bool Last, Previous; }
    static readonly ConditionalWeakTable<Window, Presses> LeftPresses = new();

    public static void BeginDrag(Visual host, PointerPressedEventArgs e) {
        var left = e.GetCurrentPoint(host).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        if (TopLevel.GetTopLevel(host) is not Window window) return;
        var afterLeft = FollowsLeftPress(window, left);
        if (!left) return;

        // Avalonia counts a press of any button towards ClickCount, so a right press then a left one also reads as 2.
        if (e.ClickCount == 2 && afterLeft) {
            e.Handled = true;
            if (!window.CanResize || window.WindowState == WindowState.FullScreen) return;

            var action = OperatingSystem.IsMacOS() ? MacWindowPreferences.ReadTitleBarDoubleClickAction() : null;
            window.WindowState = DoubleClickState(window.WindowState, action);
            return;
        }

        window.BeginMoveDrag(e);
    }

    /// Whether the press before this one, anywhere in the window, was a left press.
    static bool FollowsLeftPress(Window window, bool left) {
        if (LeftPresses.TryGetValue(window, out var presses)) return presses.Previous;
        presses = new Presses { Last = left };
        LeftPresses.Add(window, presses);
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => {
            presses.Previous = presses.Last;
            presses.Last = e.GetCurrentPoint(window).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        return false;
    }

    internal static WindowState DoubleClickState(WindowState current, string? macAction) => macAction switch {
        "None" => current,
        "Minimize" => WindowState.Minimized,
        // Avalonia maps Maximized to native window zoom on macOS.
        _ => current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
    };
}
