using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class ComposerAccountButton : UserControl {
    NavigationMethod _openMethod = NavigationMethod.Tab;

    public ComposerAccountButton() {
        InitializeComponent();
        // An opaque native popup can show its background through the panel's rounded corners.
        ((Flyout)AccountLimitsButton.Flyout!).Popup.ShouldUseOverlayLayer = true;
        AccountLimitsButton.AddHandler(PointerPressedEvent, (_, _) => _openMethod = NavigationMethod.Pointer, RoutingStrategies.Tunnel);
        AccountLimitsButton.AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key is Key.Enter or Key.Space) _openMethod = NavigationMethod.Tab;
        }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => AccountLimitsButton.Flyout?.Hide();
    }

    void OnOpened(object? sender, EventArgs e) {
        AccountLimitsScroll.Focus(_openMethod);
        _openMethod = NavigationMethod.Tab;
    }
}
