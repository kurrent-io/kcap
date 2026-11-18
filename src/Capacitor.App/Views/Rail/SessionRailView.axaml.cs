using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views.Rail;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    readonly Flyout _rowMenu;
    readonly ItemsControl _rowMenuItems;
    Button? _rowMenuAnchor;

    public SessionRailView() {
        InitializeComponent();
        KeyboardDismissible(RailHelpButton);
        foreach (var anchor in new[] { RailFilterButton, RailGroupButton, RailSortButton, RailScopeButton, RailStateButton })
            KeyboardDismissible(anchor, NavigationMethod.Pointer);
        RailGroups.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel);
        RailGroups.AddHandler(ContextRequestedEvent, OnRowsContextRequested);
        _rowMenu = (Flyout)Resources["RailRowMenu"]!;
        _rowMenuItems = (ItemsControl)_rowMenu.Content!;
        // Pointer-opened: focus for Escape without the keyboard focus ring, once the items are realized.
        _rowMenu.Opened += (_, _) => Dispatcher.UIThread.Post(() => FocusFirstItem(_rowMenuItems, NavigationMethod.Pointer), DispatcherPriority.Loaded);
        _rowMenuItems.AddHandler(KeyDownEvent, (_, e) => DismissOnEscape(_rowMenu, (Control?)_rowMenuAnchor ?? RailGroups, e, NavigationMethod.Pointer), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnRailKeyDown, RoutingStrategies.Bubble);
    }

    // A pointer-opened flyout leaves focus on its button, outside the popup, where Escape never reaches it.
    static void KeyboardDismissible(Button anchor, NavigationMethod focus = NavigationMethod.Tab) {
        if (anchor.Flyout is not Flyout { Content: Control content } flyout) return;

        flyout.Opened += (_, _) => FocusFirstItem(content, focus);
        content.AddHandler(KeyDownEvent, (_, e) => DismissOnEscape(flyout, anchor, e, focus), RoutingStrategies.Bubble);
    }

    static void FocusFirstItem(Control content, NavigationMethod method) =>
        content.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled)?.Focus(method);

    static void DismissOnEscape(FlyoutBase flyout, Control anchor, KeyEventArgs e, NavigationMethod refocus) {
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        flyout.Hide();
        anchor.Focus(refocus);
    }

    SessionRailViewModel? RailModel => (DataContext as MainWindowViewModel)?.Rail;

    static Button? RowButtonOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("railRow"));

    void OnRowsContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (RailModel is not { } rail || RowButtonOf(e.Source) is not { DataContext: RailSessionViewModel row } button) return;
        e.Handled = true;
        var items = rail.MenuFor(row);
        if (items.Count == 0) return;
        _rowMenuAnchor = button;
        _rowMenuItems.ItemsSource = items;
        _rowMenu.ShowAt(RailGroups, showAtPointer: true);
    }

    void OnRowMenuItemClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { DataContext: RailMenuItem item }) return;
        _rowMenu.Hide();
        item.Run();
        e.Handled = true;
    }

    // Ctrl/Cmd+click marks a row and Shift+click a range, for the row menu to act on together; a plain
    // click opens the row and drops the marks. Tunnelled so a marking click never reaches the row's Click.
    void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (RailModel is not { } rail || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (RowButtonOf(e.Source)?.DataContext is not RailSessionViewModel row) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) rail.ExtendMarks(row);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) rail.ToggleMark(row);
        else {
            rail.ClearMarks();
            return;
        }
        e.Handled = true;
    }

    void OnRailKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || e.Handled || RailModel is not { HasMarks: true } rail) return;
        rail.ClearMarks();
        e.Handled = true;
    }

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);


    // Button.OnClick raises ClickEvent before reading Command, and Flyout.Hide detaches the popup
    // parent and clears inherited DataContext on its rows, so capture and run Command here.
    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailHelpButton, sender, e);

    void OnWorkItemHeaderClick(object? sender, RoutedEventArgs e) {
        if (e.Source == sender && sender is Control { DataContext: RailGroupViewModel group }) group.Expand();
    }

    void OnGroupPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailGroupButton, sender, e);

    void OnSortPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailSortButton, sender, e);

    void OnScopePickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailScopeButton, sender, e);

    void OnStatePickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailStateButton, sender, e);

    void OnProjectPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailProjectButton, sender, e);

    static void RunFlyoutItem(Button anchor, object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command, CommandParameter: var parameter }
            || !command.CanExecute(parameter)) return;
        anchor.Flyout?.Hide();
        command.Execute(parameter);
        e.Handled = true;
    }

    AboutWindow? _about;

    void OnAboutClick(object? sender, RoutedEventArgs e) {
        RailHelpButton.Flyout?.Hide();
        if (_about is { } open) {
            open.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _about = new AboutWindow { DataContext = DataContext };
        _about.Closed += (_, _) => _about = null;
        _about.Show(owner);
    }
}
