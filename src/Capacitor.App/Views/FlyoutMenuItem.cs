using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

/// A button inside a flyout that runs its command and closes the menu.
public static class FlyoutMenuItem {
    // The command and its parameter are read before the menu hides: closed, its content leaves the
    // tree and its bindings let go of them.
    public static void Click(FlyoutBase? menu, object? sender, RoutedEventArgs e) {
        e.Handled = true;
        var (command, parameter) = sender is Button button ? (button.Command, button.CommandParameter) : (null, null);
        menu?.Hide();
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }
}
