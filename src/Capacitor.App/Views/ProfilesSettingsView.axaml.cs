using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class ProfilesSettingsView : UserControl {
    FlyoutBase? _openMenu;

    public ProfilesSettingsView() => InitializeComponent();

    void OnMenuOpened(object? sender, EventArgs e) => _openMenu = sender as FlyoutBase;

    void OnMenuItemClick(object? sender, RoutedEventArgs e) => FlyoutMenuItem.Click(_openMenu, sender, e);
}
