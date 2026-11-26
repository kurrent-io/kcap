using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class ArtefactsView : UserControl {
    FlyoutBase? _openMenu;

    public ArtefactsView() => InitializeComponent();

    // Any pointer or keyboard approach to the arrows re-asks the in-app switch, which raises nothing itself.
    void OnSelectorBarUsed(object? sender, RoutedEventArgs e) => (DataContext as ArtefactsTabViewModel)?.RefreshSteps();

    void OnMenuOpened(object? sender, EventArgs e) => _openMenu = sender as FlyoutBase;

    // The command and its parameter are read before the menu hides: closed, its content leaves the
    // tree and its bindings let go of them.
    void OnMenuItemClick(object? sender, RoutedEventArgs e) {
        e.Handled = true;
        var (command, parameter) = sender is Button button ? (button.Command, button.CommandParameter) : (null, null);
        _openMenu?.Hide();
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }
}
