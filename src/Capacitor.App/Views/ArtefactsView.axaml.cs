using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class ArtefactsView : UserControl {
    FlyoutBase? _openMenu;

    Window? _window;

    public ArtefactsView() => InitializeComponent();

    // The in-app switch changes only in Settings or through `kcap experimental` in a terminal, and either
    // takes focus from this window, so it is re-read when the window is active again.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    void OnWindowActivated(object? sender, EventArgs e) => (DataContext as ArtefactsTabViewModel)?.RefreshSteps();

    // The arrows also re-ask on any pointer or keyboard approach.
    void OnSelectorBarUsed(object? sender, RoutedEventArgs e) => (DataContext as ArtefactsTabViewModel)?.RefreshSteps();

    void OnMenuOpened(object? sender, EventArgs e) => _openMenu = sender as FlyoutBase;

    void OnMenuItemClick(object? sender, RoutedEventArgs e) => FlyoutMenuItem.Click(_openMenu, sender, e);
}
