using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views.Rail;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
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
        _ = new RowDrag(this, RailGroups, RailDragGhost, RailDragGhostSnapshot, "railRow",
            row => row is RailSessionViewModel session && RailModel is { ShowsWorkItems: true } rail ? rail.Picked(session) : null,
            OutlinedTargetAt, OnDragStarted, OnDragEnded, () => RailModel?.ClearMarks(),
            keyOf: row => row is RailSessionViewModel session ? session.Key : row, compactGhostWidth: 320, glides: false);
        AddHandler(KeyDownEvent, OnRailKeyDown, RoutingStrategies.Bubble);
    }

    static void KeyboardDismissible(Button anchor, NavigationMethod focus = NavigationMethod.Tab) {
        if (anchor.Flyout is not Flyout { Content: Control content } flyout) return;

        flyout.Opened += (_, _) => FlyoutKeyboard.FocusFirstItem(content, focus);
        content.AddHandler(KeyDownEvent, (_, e) => FlyoutKeyboard.DismissOnEscape(flyout, anchor, e, focus), RoutingStrategies.Bubble);
    }

    SessionRailViewModel? RailModel => (DataContext as MainWindowViewModel)?.Rail;

    // A transition, not an animation: an animation's value drops back to the base value for a frame when it ends.
    readonly Transitions _groupsFadeIn = [new DoubleTransition { Property = OpacityProperty, Duration = Motion.Arrive.Duration, Easing = Motion.Arrive.Easing }];
    SessionRailViewModel? _rail;
    int _projectShows;

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        if (_rail is not null) _rail.ProjectShowing -= OnProjectShowing;
        _rail = RailModel;
        if (_rail is not null) _rail.ProjectShowing += OnProjectShowing;
    }

    // Hidden until ContextIdle, which runs only once the regroup's posted passes have drained.
    void OnProjectShowing() {
        var show = ++_projectShows;
        RailGroups.Transitions = null;
        RailGroups.Opacity = 0;
        Dispatcher.UIThread.Post(() => {
            if (show != _projectShows) return;
            RailGroups.Transitions = _groupsFadeIn;
            RailGroups.Opacity = 1;
        }, DispatcherPriority.ContextIdle);
    }

    void OnSessionRowAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is Button { DataContext: RailSessionViewModel row } button && RailModel?.TakeArrival(row.Key) == true)
            _ = Motion.Arrive.RunAsync(button, CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    static Button? RowButtonOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("railRow"));

    // Ctrl/Cmd+click marks a row and Shift+click a range, for the row menu to act on together; a plain
    // click opens the row and drops the marks. Tunnelled, and handled, so a marking click never reaches the
    // row's Click or starts a drag. A marked row may be dragged with its marks, so a plain press on one
    // clears them only on release.
    void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (RailModel is not { } rail || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (RowButtonOf(e.Source)?.DataContext is not RailSessionViewModel row) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) rail.ExtendMarks(row);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) rail.ToggleMark(row);
        else {
            if (!row.IsMarked) rail.ClearMarks();
            return;
        }
        e.Handled = true;
    }

    void OnDragStarted(object carried) {
        if (carried is not IReadOnlyList<RailSessionViewModel> { Count: > 0 } rows || RailModel is not { } rail) return;
        RailDragGhostCount.IsVisible = rows.Count > 1;
        RailDragGhostCountText.Text = rows.Count.ToString(CultureInfo.InvariantCulture);
        RailDropOut.IsVisible = rail.DropOn(rows, null) is not null && !rail.Groups.Any(g => !g.IsWorkItem);
    }

    void OnDragEnded() {
        RailDropOut.IsVisible = false;
        Outline(null);
    }

    RowDropTarget? OutlinedTargetAt(object carried, Point at) {
        var target = DropTargetAt(carried, at);
        Outline(target?.Zone == RailDropOut ? null : target?.Zone);
        return target;
    }

    Control? _outlined;

    void Outline(Control? zone) {
        if (zone == _outlined) return;
        _outlined = zone;
        if (zone?.TranslatePoint(default, RailDragLayer) is not { } at) {
            RailDropOutline.IsVisible = false;
            return;
        }
        Canvas.SetLeft(RailDropOutline, at.X);
        Canvas.SetTop(RailDropOutline, at.Y);
        RailDropOutline.Width = zone.Bounds.Width;
        RailDropOutline.Height = zone.Bounds.Height;
        RailDropOutline.IsVisible = true;
    }

    RowDropTarget? DropTargetAt(object carried, Point at) {
        if (RailModel is not { } rail || carried is not IReadOnlyList<RailSessionViewModel> rows) return null;
        foreach (var visual in (this.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors() ?? []) {
            if (visual == RailDropOut)
                return rail.DropOn(rows, null) is { } drop ? new(RailDropOut, drop) : null;
            if (visual is Border { DataContext: RailGroupViewModel group } zone && zone.Classes.Contains("railGroup")) {
                var drop = rail.DropOn(rows, group.WorkItemId) ?? (rows.All(group.Sessions.Contains) ? Stay : null);
                return drop is null ? null : new(zone, drop);
            }
        }
        return null;
    }

    // Dropped back on the group they are in, the rows stay.
    static void Stay() { }

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
