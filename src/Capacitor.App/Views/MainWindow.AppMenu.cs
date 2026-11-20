using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

public partial class MainWindow {
    readonly AltTap _altTap = new();
    IDisposable? _appMenuRows;
    IInputElement? _focusBeforeAppMenu;

    Flyout AppMenuPopup => (Flyout)AppMenuButton.Flyout!;

    // The popup is its own top level on a desktop, so its keys never reach the window's handlers:
    // the tap that closes the menu and the arrow keys are handled on its content as well.
    void BindAppMenu() {
        var menu = AppMenuPopup;
        var content = (Control)menu.Content!;
        menu.Opening += (_, _) => {
            _focusBeforeAppMenu = FocusManager?.GetFocusedElement();
            _appMenuRows?.Dispose();
            _appMenuRows = AppMenuFlyout.Populate(AppMenuRows, AppMenuFlyout.MenusFor(this), menu.Hide);
        };
        menu.Opened += (_, _) => AppMenuFlyout.MoveFocus(AppMenuRows, down: true);
        menu.Closed += (_, _) => {
            _altTap.Cancel();
            if (_focusBeforeAppMenu is Visual previous && previous.IsAttachedToVisualTree()) ((IInputElement)previous).Focus();
            _focusBeforeAppMenu = null;
        };
        content.AddHandler(KeyDownEvent, OnAppMenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        content.AddHandler(KeyUpEvent, (_, e) => {
            if (!_altTap.KeyUp(e.Key)) return;
            e.Handled = true;
            menu.Hide();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        AddHandler(KeyDownEvent, (_, e) => _altTap.KeyDown(e.Key, e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => {
            if (!_altTap.KeyUp(e.Key) || !CanOpenAppMenu) return;
            e.Handled = true;
            ToggleAppMenu();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _altTap.Cancel(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => _altTap.Reset();
    }

    AboutWindow? _about;

    /// One About window per main window, brought forward when it is already open.
    internal void ShowAbout() {
        if (_about is { } open) {
            open.Activate();
            return;
        }
        _about = new AboutWindow { DataContext = DataContext };
        _about.Closed += (_, _) => _about = null;
        _about.Show(this);
    }

    // Setup covers the window's controls until it hands over.
    bool CanOpenAppMenu => AppMenuButton.IsEffectivelyVisible && !OnboardingPane.IsVisible;

    void ToggleAppMenu() {
        if (AppMenuPopup.IsOpen) AppMenuPopup.Hide();
        else AppMenuPopup.ShowAt(AppMenuButton);
    }

    void OnAppMenuKeyDown(object? sender, KeyEventArgs e) {
        _altTap.KeyDown(e.Key, e.KeyModifiers);
        switch (e.Key) {
            case Key.Escape:
                AppMenuPopup.Hide();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                e.Handled = AppMenuFlyout.MoveFocus(AppMenuRows, e.Key == Key.Down);
                break;
        }
    }
}
