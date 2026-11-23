using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Pages;

namespace Capacitor.App.Views.Pages;

/// A work item's page drawn over the main area. DataContext is its WorkItemPageViewModel.
public partial class WorkItemPageView : UserControl, IRowDropSurface {
    WorkItemPageViewModel? _page;
    Control? _outlined;

    public WorkItemPageView() {
        InitializeComponent();
        // Tunnels, since the field handles Enter itself before a bubbling handler sees it.
        AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], DetailsPane, HideButton, ContextSplitter, ContextShowButton);
        _ = new RowMenu(this, "sessionRow", row =>
            row is WorkItemPageSession session && DataContext is WorkItemPageViewModel page ? page.MenuFor(session) : []);
        _ = new RowMenu(this, "partRow", row =>
            row is WorkItemPagePart part && DataContext is WorkItemPageViewModel page ? page.MenuFor(part, PickWaits) : []);
        _ = new RowDrag(this, WorkItemSlices, DragGhost, DragGhostSnapshot, "dragRow", row => row, OutlinedTargetAt,
            started: _ => _page?.HoldRebuilds(true),
            ended: () => {
                Outline(null);
                _page?.HoldRebuilds(false);
            },
            keyOf: row => row switch {
                WorkItemPagePart part       => part.Id,
                WorkItemPageSession session => session.SessionId,
                _                           => row,
            },
            compactGhostWidth: 440, glides: false);
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        if (_page is not null) _page.PropertyChanged -= OnPageChanged;
        _page = DataContext as WorkItemPageViewModel;
        if (_page is not null) _page.PropertyChanged += OnPageChanged;
    }

    void OnPageChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(WorkItemPageViewModel.Layout))
            _ = Motion.Standard.RunAsync(_page!.IsBoard ? WorkItemBoardView : _page.IsGraph ? WorkItemGraphView : WorkItemGroups,
                CancellationToken.None, (OpacityProperty, 0d, 1d));
    }

    // Opened after the fold has loaded, since a fold that is not yet loaded snaps open.
    void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is not Control { DataContext: WorkItemPagePart part } row) return;
        if (part.Arrives) _ = Motion.Arrive.RunAsync(row, CancellationToken.None, (OpacityProperty, 0d, 1d));
        if (part.Opens) Dispatcher.UIThread.Post(part.Open, DispatcherPriority.Background);
        if (part.Reveals && row is StackPanel panel) Dispatcher.UIThread.Post(() => _ = RevealAsync(panel), DispatcherPriority.Background);
    }

    // Posted after the row's Open, so its fold has measured the height it unfolds to.
    async Task RevealAsync(StackPanel row) {
        if (!row.IsEffectivelyVisible) return;
        if (row.Children[0] is Button { Presenter: { } presenter } && this.FindResource("KcapSelectionBrush") is ISolidColorBrush selection)
            _ = Flash(selection).RunAsync(presenter);
        if (row.FindAncestorOfType<ScrollViewer>() is not { } scroller || row.TranslatePoint(default, scroller) is not { } at) return;
        var height = row.Children[0].Bounds.Height + row.Children[1].DesiredSize.Height;
        if (at.Y < 0 || at.Y + height > scroller.Viewport.Height) await ScrollToAsync(scroller, scroller.Offset.Y + at.Y - 16);
    }

    static Animation Flash(ISolidColorBrush brush) => new() {
        Duration = TimeSpan.FromMilliseconds(1600),
        Children = {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(BackgroundProperty, brush) } },
            new KeyFrame { Cue = new Cue(0.5d), Setters = { new Setter(BackgroundProperty, brush) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(BackgroundProperty, new SolidColorBrush(brush.Color, 0)) } },
        },
    };

    // Stepped per frame rather than animated, so each step is clamped to the extent as the fold grows it.
    static async Task ScrollToAsync(ScrollViewer scroller, double y) {
        if (TopLevel.GetTopLevel(scroller) is not { } top) return;
        var from = scroller.Offset.Y;
        TimeSpan? start = null;
        for (double t = 0; t < 1;) {
            var frame = new TaskCompletionSource<TimeSpan>();
            top.RequestAnimationFrame(frame.SetResult);
            var now = await frame.Task;
            t = Math.Min(1, (now - (start ??= now)) / Motion.Standard.Duration);
            scroller.Offset = scroller.Offset.WithY(from + (y - from) * Motion.Standard.Easing.Ease(t));
        }
    }

    // A rebuild replaces the group, and with it the field being typed in, so the new field takes focus back.
    static void OnComposerAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is not TextBox { DataContext: WorkItemPageGroupViewModel { IsComposing: true } } box) return;
        Dispatcher.UIThread.Post(() => {
            if (!box.IsEffectivelyVisible) return;
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
        if (_page is null || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true)?.Classes.Contains("composer") != true) return;
        if (e.Key == Key.Escape) _page.CloseComposer();
        else if (e.Key == Key.Enter) ((ICommand)_page.AddPartCommand).Execute(null);
        else return;
        e.Handled = true;
    }

    // Checked once focus has settled: a field replaced by a rebuild hands focus to its successor, the status picker
    // opens a popup, and another add button moves the field itself; closing under that button would swallow its click.
    void OnComposerLostFocus(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => {
        if (_page is null || !string.IsNullOrWhiteSpace(_page.NewPartTitle)) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused
            && (focused.Classes.Contains("addSlice") || focused.FindLogicalAncestorOfType<Popup>() is not null
                || focused.GetSelfAndVisualAncestors().OfType<Control>().Any(v => v.Classes.Contains("composerRow"))))
            return;
        _page.CloseComposer();
    }, DispatcherPriority.Background);

    // Runs before the flyout hides: hiding detaches the row, and a command bound through the page would go with it.
    void OnComposerStatusClick(object? sender, RoutedEventArgs e) {
        if (sender is Control { DataContext: WorkItemPageStatus picked }) _page?.ComposeCommand.Execute(picked.Status).Subscribe();
        OnFlyoutItemClick(sender, e);
    }

    void OnWaitsClick(object? sender, RoutedEventArgs e) {
        if (sender is Control { DataContext: WorkItemPagePart part }) PickWaits(part);
        e.Handled = true;
    }

    // Under the row's right end in the list, where its count shows; under a card or a graph box otherwise. Posted so
    // a menu that asked for it has closed first, or the picker is dismissed with it, and the row is found by id then,
    // since a read in between replaces the rows.
    void PickWaits(WorkItemPagePart part) => Dispatcher.UIThread.Post(() => {
        var row = WorkItemSlices.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            button.Classes.Contains("partRow") && button.DataContext is WorkItemPagePart shown && shown.Id == part.Id && button.IsEffectivelyVisible);
        if (row is null || this.FindResource("WaitPicker") is not Flyout picker) return;
        picker.Placement = _page?.IsList == true ? PlacementMode.BottomEdgeAlignedRight : PlacementMode.BottomEdgeAlignedLeft;
        picker.ShowAt(row);
    }, DispatcherPriority.Background);

    static void OnWaitSearchAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is TextBox { IsVisible: true } box) Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
    }

    void OnWaitPickerOpened(object? sender, EventArgs e) => _page?.HoldRebuilds(true);
    void OnWaitPickerClosed(object? sender, EventArgs e) => _page?.HoldRebuilds(false);

    void OnFlyoutItemClick(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.FindLogicalAncestorOfType<Popup>()?.PlacementTarget as Button)?.Flyout?.Hide();

    RowDropTarget? IRowDropSurface.TargetAt(object carried, Point at) => OutlinedTargetAt(
        carried is IReadOnlyList<RailSessionViewModel> rows ? (object)rows.Select(row => row.Row.SessionId).OfType<string>().ToList() : carried, at);

    void IRowDropSurface.DragLeft() => Outline(null);

    // The board's columns and cards mark a drop themselves.
    RowDropTarget? OutlinedTargetAt(object carried, Point at) {
        var target = DropTargetAt(carried, at);
        Outline(_page?.IsList == true ? target?.Zone : null);
        return target;
    }

    void Outline(Control? zone) {
        if (zone == _outlined) return;
        _outlined = zone;
        if (zone?.TranslatePoint(default, DragLayer) is not { } at) {
            DropOutline.IsVisible = false;
            return;
        }
        Canvas.SetLeft(DropOutline, at.X - 4);
        Canvas.SetTop(DropOutline, at.Y - 2);
        DropOutline.Width = zone.Bounds.Width + 8;
        DropOutline.Height = zone.Bounds.Height + 4;
        DropOutline.IsVisible = true;
    }

    // The nearest slice or group under the pointer; a slice passes over slices to the group they are in.
    RowDropTarget? DropTargetAt(object carried, Point at) {
        if (DataContext is not WorkItemPageViewModel page) return null;
        foreach (var visual in (this.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors() ?? []) {
            if (visual is not Control { DataContext: { } target } zone || !(zone.Classes.Contains("part") || zone.Classes.Contains("group"))) continue;
            if (carried is WorkItemPagePart && target is WorkItemPagePart && !page.IsGraph) continue;
            return page.DropOn(carried, target) is { } drop ? new(zone, drop) : null;
        }
        return null;
    }
}
