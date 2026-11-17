using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class SessionPartChip : UserControl {
    public SessionPartChip() => InitializeComponent();

    void OnMenuItemClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command, CommandParameter: var parameter } || !command.CanExecute(parameter)) return;
        PartChipButton.Flyout?.Hide();
        command.Execute(parameter);
        e.Handled = true;
    }
}
