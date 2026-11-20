using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Pages;

namespace Capacitor.App.Views.Pages;

/// A work item's page drawn over the main area. DataContext is its WorkItemPageViewModel.
public partial class WorkItemPageView : UserControl {
    readonly Reflow _reflow;
    WorkItemPageViewModel? _page;
    object? _carried;
    double _roomFor;
    Control? _roomAt;
    bool _roomBelow;
    RowDropTarget? _roomTarget;
    Control? _origin;

    public WorkItemPageView() {
        InitializeComponent();
        _reflow = new Reflow(WorkItemSlices, FlowKey);
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], DetailsPane, HideButton, ContextSplitter, ContextShowButton);
        _ = new RowMenu(this, "sessionRow", row =>
            row is WorkItemPageSession session && DataContext is WorkItemPageViewModel page ? page.MenuFor(session) : []);
        _ = new RowMenu(this, "partRow", row =>
            row is WorkItemPagePart part && DataContext is WorkItemPageViewModel page ? page.MenuFor(part) : []);
        _ = new RowDrag(this, WorkItemSlices, DragGhost, DragGhostSnapshot, "dragRow", row => row, TargetMakingRoom,
            started: carried => {
                _carried = FlowKey(carried);
                var row = Rows().FirstOrDefault(row => Equals(FlowKey(row), _carried));
                _roomFor = row?.Bounds.Height ?? 0;
                Collapse(row?.GetVisualParent() as Control);
                _page?.HoldRebuilds(true);
            },
            ended: () => {
                MakeRoom(null, false, null);
                Collapse(null);
                _page?.HoldRebuilds(false);
            },
            keyOf: row => row switch {
                WorkItemPagePart part       => part.Id,
                WorkItemPageSession session => session.SessionId,
                _                           => row,
            });
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        if (_page is not null) {
            _page.PropertyChanging -= OnPageChanging;
            _page.PropertyChanged -= OnPageChanged;
        }
        _page = DataContext as WorkItemPageViewModel;
        if (_page is null) return;
        _page.PropertyChanging += OnPageChanging;
        _page.PropertyChanged += OnPageChanged;
    }

    // A rebuild sets Groups first and Columns last. The dragged row is left to the drag, which lands its ghost on it.
    void OnPageChanging(object? sender, PropertyChangingEventArgs e) {
        if (e.PropertyName == nameof(WorkItemPageViewModel.Groups)) _reflow.Capture();
    }

    void OnPageChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(WorkItemPageViewModel.Columns)) {
            _reflow.Play(_carried);
            _carried = null;
        }
        else if (e.PropertyName == nameof(WorkItemPageViewModel.IsBoard))
            _ = Motion.Standard.RunAsync(_page!.IsBoard ? WorkItemBoardView : WorkItemGroups, CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    static object? FlowKey(Control control) => control.Classes.Contains("flow") ? FlowKey(control.DataContext) : null;

    static object? FlowKey(object? row) => row switch {
        WorkItemPagePart part              => (nameof(WorkItemPagePart), part.Id),
        WorkItemPageSession session        => (nameof(WorkItemPageSession), session.SessionId),
        WorkItemPageGroupViewModel group   => (nameof(WorkItemPageGroupViewModel), group.Key),
        _                                  => null,
    };

    void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is Control { DataContext: WorkItemPagePart { Arrives: true } or WorkItemPageSession { Arrives: true } } row)
            _ = Motion.Arrive.RunAsync(row, CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    void OnFlyoutItemClick(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.FindLogicalAncestorOfType<Popup>()?.PlacementTarget as Button)?.Flyout?.Hide();

    IEnumerable<Control> Rows() => WorkItemSlices.GetVisualDescendants().OfType<Control>().Where(row => row.IsEffectivelyVisible);

    // A slice dropped between slices opens a gap where it will land. While the pointer is in that gap the target holds,
    // or the gap would close under it and open again.
    RowDropTarget? TargetMakingRoom(object carried, Point at) {
        if (_roomTarget is not null && InRoom(at)) return _roomTarget;
        var target = DropTargetAt(carried, at);
        if (target is { Edge: RowDropEdge.Top or RowDropEdge.Bottom } edge
            && edge.Zone.GetVisualParent() is Control item && item.GetVisualParent() is Panel { Children: var siblings }) {
            var index = siblings.IndexOf(item) + (edge.Edge == RowDropEdge.Bottom ? 1 : 0);
            MakeRoom(index < siblings.Count ? siblings[index] : siblings[^1], index == siblings.Count, target);
        }
        else MakeRoom(null, false, null);
        return target;
    }

    // The gap is a margin on the row it opens above, or under the last row, so the rows really move and the lane grows.
    void MakeRoom(Control? at, bool below, RowDropTarget? target) {
        _roomTarget = target;
        if (at == _roomAt && below == _roomBelow) return;
        if (_roomAt is not null) _roomAt.Margin = default;
        (_roomAt, _roomBelow) = (at, below);
        if (at is null) return;
        var room = _roomFor + SpacingOf(at);
        Ease(at);
        at.Margin = below ? new Thickness(0, 0, 0, room) : new Thickness(0, room, 0, 0);
    }

    // The dragged row's place closes while it is away, so the only gap is where it will land.
    void Collapse(Control? item) {
        if (_origin is not null) {
            _origin.Opacity = 1;
            _origin.IsHitTestVisible = true;
            _origin.Margin = default;
        }
        _origin = item;
        if (item is null) return;
        Ease(item);
        item.Opacity = 0;
        item.IsHitTestVisible = false;
        item.Margin = new Thickness(0, 0, 0, -(item.Bounds.Height + SpacingOf(item)));
    }

    static double SpacingOf(Control item) => item.GetVisualParent() is StackPanel panel ? panel.Spacing : 0;

    static void Ease(Control item) =>
        item.Transitions ??= [new ThicknessTransition { Property = MarginProperty, Duration = Motion.Standard.Duration, Easing = Motion.Standard.Easing }];

    bool InRoom(Point at) {
        if (_roomAt?.TranslatePoint(default, this) is not { } row) return false;
        var room = _roomFor + SpacingOf(_roomAt);
        var top = _roomBelow ? row.Y + _roomAt.Bounds.Height : row.Y - room;
        return at.X >= row.X && at.X <= row.X + _roomAt.Bounds.Width && at.Y >= top && at.Y <= top + room;
    }

    // The nearest slice or group under the pointer; a slice dropped on a slice goes above or below it by which half it's in.
    RowDropTarget? DropTargetAt(object carried, Point at) {
        if (DataContext is not WorkItemPageViewModel page) return null;
        foreach (var visual in (this.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors() ?? []) {
            if (visual is not Control { DataContext: { } target } zone || !(zone.Classes.Contains("part") || zone.Classes.Contains("group"))) continue;
            if (carried is not WorkItemPagePart || target is not WorkItemPagePart) return page.DropOn(carried, target, false) is { } drop ? new(zone, drop) : null;
            var after = at.Y > zone.TranslatePoint(new Point(0, zone.Bounds.Height / 2), this)?.Y == true;
            return page.DropOn(carried, target, after) is { } placed ? new(zone, placed, after ? RowDropEdge.Bottom : RowDropEdge.Top) : null;
        }
        return null;
    }
}
