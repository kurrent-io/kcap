using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views.Rail;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    RailSessionViewModel? _pressRow;
    Point _pressAt;
    IReadOnlyList<RailSessionViewModel>? _dragRows;
    IPointer? _dragPointer;
    List<Button> _lifted = [];
    Control? _dropOver;

    public SessionRailView() {
        InitializeComponent();
        KeyboardDismissible(RailHelpButton);
        foreach (var anchor in new[] { RailFilterButton, RailGroupButton, RailSortButton, RailScopeButton, RailStateButton })
            KeyboardDismissible(anchor, NavigationMethod.Pointer);
        RailGroups.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel);
        _ = new RowMenu(RailGroups, "railRow", row => (row, RailModel) switch {
            (RailSessionViewModel session, { } rail) => rail.MenuFor(session),
            (RailGroupViewModel group, { } rail) => rail.MenuFor(group),
            _ => [],
        });
        AddHandler(PointerMovedEvent, OnDragPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnDragPointerReleased, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => EndDrag();
        AddHandler(KeyDownEvent, OnRailKeyDown, RoutingStrategies.Bubble);
    }

    static void KeyboardDismissible(Button anchor, NavigationMethod focus = NavigationMethod.Tab) {
        if (anchor.Flyout is not Flyout { Content: Control content } flyout) return;

        flyout.Opened += (_, _) => FlyoutKeyboard.FocusFirstItem(content, focus);
        content.AddHandler(KeyDownEvent, (_, e) => FlyoutKeyboard.DismissOnEscape(flyout, anchor, e, focus), RoutingStrategies.Bubble);
    }

    SessionRailViewModel? RailModel => (DataContext as MainWindowViewModel)?.Rail;

    void OnSessionRowAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is Button { DataContext: RailSessionViewModel row } button && RailModel?.TakeArrival(row.Key) == true)
            _ = Motion.Arrive.RunAsync(button, CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    static Button? RowButtonOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("railRow"));

    // Ctrl/Cmd+click marks a row and Shift+click a range, for the row menu to act on together; a plain
    // click opens the row and drops the marks. Tunnelled so a marking click never reaches the row's Click.
    void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (RailModel is not { } rail || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (RowButtonOf(e.Source)?.DataContext is not RailSessionViewModel row) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) rail.ExtendMarks(row);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) rail.ToggleMark(row);
        else {
            // A marked row may be dragged with its marks, so a plain press on one clears them only on release.
            _pressRow = row;
            _pressAt = e.GetPosition(this);
            if (!row.IsMarked) rail.ClearMarks();
            return;
        }
        e.Handled = true;
    }

    void OnDragPointerMoved(object? sender, PointerEventArgs e) {
        var at = e.GetPosition(this);
        if (_dragRows is null) {
            if (_pressRow is not { } row || RailModel is not { ShowsWorkItems: true } rail
                || (Math.Abs(at.X - _pressAt.X) < 4 && Math.Abs(at.Y - _pressAt.Y) < 4)) return;
            StartDrag(rail, row, e.Pointer);
        }
        Canvas.SetLeft(RailDragGhost, at.X - 14);
        Canvas.SetTop(RailDragGhost, at.Y - 16);
        ShowDropOver(DropTargetAt(at)?.Zone);
        e.Handled = true;
    }

    void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (_dragRows is null) {
            if (_pressRow is null) return;
            _pressRow = null;
            RailModel?.ClearMarks();
            return;
        }
        var drop = DropTargetAt(e.GetPosition(this))?.Drop;
        EndDrag();
        drop?.Invoke();
        e.Handled = true;
    }

    // Captured by the rail, the row's button loses its press and never reads the drop's release as a click.
    void StartDrag(SessionRailViewModel rail, RailSessionViewModel row, IPointer pointer) {
        _pressRow = null;
        var rows = _dragRows = rail.Picked(row);
        _dragPointer = pointer;
        pointer.Capture(this);
        RailDragGhost.DataContext = row;
        RailDragGhostLabel.Text = rows.Count > 1 ? $"{rows.Count} sessions" : row.Primary;
        RailDragGhost.IsVisible = true;
        _lifted = [.. RailGroups.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("railRow") && b.DataContext is RailSessionViewModel r && rows.Contains(r))];
        foreach (var button in _lifted) button.Classes.Add("lifted");
        RailDropOut.IsVisible = rail.DropOn(rows, null) is not null && !rail.Groups.Any(g => !g.IsWorkItem);
    }

    void EndDrag() {
        _pressRow = null;
        if (_dragRows is null) return;
        _dragRows = null;
        RailDragGhost.IsVisible = false;
        RailDragGhost.DataContext = null;
        RailDropOut.IsVisible = false;
        foreach (var button in _lifted) button.Classes.Remove("lifted");
        _lifted = [];
        ShowDropOver(null);
        _dragPointer?.Capture(null);
        _dragPointer = null;
    }

    (Control Zone, Action Drop)? DropTargetAt(Point at) {
        if (RailModel is not { } rail || _dragRows is not { } rows) return null;
        foreach (var visual in (this.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors() ?? []) {
            if (visual == RailDropOut)
                return rail.DropOn(rows, null) is { } drop ? (RailDropOut, drop) : null;
            if (visual is Border { DataContext: RailGroupViewModel group } zone && zone.Classes.Contains("railGroup"))
                return rail.DropOn(rows, group.WorkItemId) is { } drop ? (zone, drop) : null;
        }
        return null;
    }

    void ShowDropOver(Control? zone) {
        if (_dropOver == zone) return;
        _dropOver?.Classes.Remove("dropOver");
        _dropOver = zone;
        zone?.Classes.Add("dropOver");
    }

    void OnRailKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && _dragRows is not null) {
            EndDrag();
            e.Handled = true;
            return;
        }
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

    void OnGroupPickerItemClick(object? sender, RoutedEventArgs e) => RunFilterItem(RailGroupButton, sender, e);

    void OnSortPickerItemClick(object? sender, RoutedEventArgs e) => RunFilterItem(RailSortButton, sender, e);

    void OnScopePickerItemClick(object? sender, RoutedEventArgs e) => RunFilterItem(RailScopeButton, sender, e);

    void OnStatePickerItemClick(object? sender, RoutedEventArgs e) => RunFilterItem(RailStateButton, sender, e);

    void RunFilterItem(Button anchor, object? sender, RoutedEventArgs e) {
        RunFlyoutItem(anchor, sender, e);
        if (e.Handled) RailFilterButton.Flyout?.Hide();
    }

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
