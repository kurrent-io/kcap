using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class NextWorkCardView : UserControl {
    public NextWorkCardView() => InitializeComponent();

    /// A menu row runs its command and closes the menu, as a menu item does.
    void OnMenuItemClick(object? sender, RoutedEventArgs e) => CardMore.Flyout?.Hide();

    /// The open menu takes the pointer and focus from the row; the row keeps its actions shown, the menu's own trigger
    /// among them, until the menu closes.
    void OnMenuOpened(object? sender, EventArgs e) => RowHost.Classes.Set("menuOpen", true);

    void OnMenuClosed(object? sender, EventArgs e) => RowHost.Classes.Set("menuOpen", false);
}
