using Avalonia;
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
    public WorkItemPageView() {
        InitializeComponent();
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], DetailsPane, HideButton, ContextSplitter, ContextShowButton);
        _ = new RowMenu(this, "sessionRow", row =>
            row is WorkItemPageSession session && DataContext is WorkItemPageViewModel page ? page.MenuFor(session) : []);
        _ = new RowMenu(this, "partRow", row =>
            row is WorkItemPagePart part && DataContext is WorkItemPageViewModel page ? page.MenuFor(part) : []);
        _ = new RowDrag(this, WorkItemGroups, DragGhost, "dragRow", row => row, DropTargetAt,
            started: carried => DragGhostLabel.Text = carried switch {
                WorkItemPagePart part       => part.Title,
                WorkItemPageSession session => session.Title,
                _                           => "",
            },
            line: DropLine);
    }

    void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is Control { DataContext: WorkItemPagePart { Arrives: true } or WorkItemPageSession { Arrives: true } } row)
            _ = Motion.Arrive.RunAsync(row, CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    void OnFlyoutItemClick(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.FindLogicalAncestorOfType<Popup>()?.PlacementTarget as Button)?.Flyout?.Hide();

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
