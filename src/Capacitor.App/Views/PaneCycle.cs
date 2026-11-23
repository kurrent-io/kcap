using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// F6 moves focus to the next of the window's panes (the session rail, transcript, composer and work
/// pane, marked by their landmark type), Shift+F6 to the previous one, wrapping at either end.
public static class PaneCycle {
    static readonly AutomationLandmarkType[] Panes =
        [AutomationLandmarkType.Navigation, AutomationLandmarkType.Main, AutomationLandmarkType.Form, AutomationLandmarkType.Complementary];

    public static void Attach(Window window) =>
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => {
            if (e.Key != Key.F6 || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
            e.Handled = Move(window, e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
        }, RoutingStrategies.Tunnel);

    public static bool Move(Window window, int step) {
        var panes = window.GetVisualDescendants().OfType<Control>()
            .Where(c => AutomationProperties.GetLandmarkType(c) is { } type && Panes.Contains(type) && c.IsEffectivelyVisible)
            .Where(c => FirstFocusable(c) is not null)
            .ToList();
        if (panes.Count == 0) return false;
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        var at = focused is null ? -1 : panes.FindIndex(p => ReferenceEquals(p, focused) || p.IsVisualAncestorOf(focused));
        var next = at < 0 ? (step > 0 ? 0 : panes.Count - 1) : (at + step + panes.Count) % panes.Count;
        // Tab, not Directional: a pane that is one Tab stop (rail, transcript) lands where Tab would.
        return FirstFocusable(panes[next])?.Focus(NavigationMethod.Tab) == true;
    }

    static Control? FirstFocusable(Control pane) => pane.GetSelfAndVisualDescendants().OfType<Control>()
        .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(c));
}
