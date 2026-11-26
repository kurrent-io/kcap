using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// The transcript is one Tab stop, so reaching the composer takes the same presses however many
/// messages are on screen. Each message is its own Tab scope: Tab walks a card's controls, then
/// leaves the transcript. Up and Down move to the previous or next message that has something to
/// focus, scrolling it into view; keys a message handles itself are left to it.
public static class ChatTranscriptKeyboard {
    // How many messages to look past for one with a focusable control before giving up.
    const int SearchLimit = 50;

    public static void Attach(ItemsControl transcript) {
        KeyboardNavigation.SetTabNavigation(transcript, KeyboardNavigationMode.Once);
        transcript.ContainerPrepared += (_, e) => KeyboardNavigation.SetTabNavigation(e.Container, KeyboardNavigationMode.Local);
        transcript.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(transcript, e), RoutingStrategies.Bubble);
    }

    static void OnKeyDown(ItemsControl transcript, KeyEventArgs e) {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down)) return;
        if (TopLevel.GetTopLevel(transcript)?.FocusManager.GetFocusedElement() is not Control focused || focused is TextBox) return;
        if (IndexOf(transcript, focused) is not { } at) return;

        e.Handled = true;
        var step = e.Key == Key.Down ? 1 : -1;
        for (var i = at + step; i >= 0 && i < transcript.ItemCount && Math.Abs(i - at) <= SearchLimit; i += step) {
            if (transcript.ItemsPanelRoot is ChatVirtualizingPanel panel) {
                if (!panel.TryFocusRow(i, FocusFirst)) continue;
            } else {
                if (transcript.ContainerFromIndex(i) is not { } container || FocusFirst(container) is not { } target) continue;
                target.BringIntoView();
            }
            return;
        }
    }

    static int? IndexOf(ItemsControl transcript, Control focused) {
        foreach (var ancestor in focused.GetSelfAndVisualAncestors().OfType<Control>()) {
            if (ReferenceEquals(ancestor, transcript)) return null;
            var index = transcript.IndexFromContainer(ancestor);
            if (index >= 0) return index;
        }
        return null;
    }

    static Control? FocusFirst(Control container) {
        foreach (var control in container.GetSelfAndVisualDescendants().OfType<Control>()) {
            if (control is not { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } or TextBox
                || !control.Focus(NavigationMethod.Directional)) continue;
            return control;
        }
        return null;
    }
}
