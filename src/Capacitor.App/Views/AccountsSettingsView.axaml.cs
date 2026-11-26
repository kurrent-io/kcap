using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class AccountsSettingsView : UserControl {
    FlyoutBase? _openMenu;

    public AccountsSettingsView() => InitializeComponent();

    void OnMenuOpened(object? sender, EventArgs e) => _openMenu = sender as FlyoutBase;

    void OnAddMenuOpening(object? sender, EventArgs e) => (DataContext as AccountsSettingsViewModel)?.RefreshCandidates();

    void OnMenuItemClick(object? sender, RoutedEventArgs e) => FlyoutMenuItem.Click(_openMenu, sender, e);
}
