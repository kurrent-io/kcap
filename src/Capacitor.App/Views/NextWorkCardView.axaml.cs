using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class NextWorkCardView : UserControl {
    public NextWorkCardView() => InitializeComponent();

    /// A menu row runs its command and closes the menu, as a menu item does.
    void OnMenuItemClick(object? sender, RoutedEventArgs e) => CardMore.Flyout?.Hide();
}
