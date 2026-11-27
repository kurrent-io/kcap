using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views.Inbox;

public partial class InboxSettingsWindow : Window {
    public InboxSettingsWindow() => InitializeComponent();

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
