using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    readonly SerialDisposable _searchLabel = new();

    public SessionRailView() {
        InitializeComponent();
        DataContextChanged += (_, _) => BindSearchLabel();
        foreach (var anchor in new[] { RailHelpButton, RailGroupButton, RailSortButton }) KeyboardDismissible(anchor);
    }

    /// Calls <paramref name="painted"/> once, on the UI thread, with the time the first frame showing a
    /// session row was drawn: a row inside the scrolled viewport of a visible rail. Data alone does not
    /// count: a collapsed rail paints nothing, and folded groups can fill the viewport with every
    /// expanded row below it.
    public void OnFirstSessionsPainted(TimeProvider time, Action<DateTimeOffset> painted) {
        var rowLists = new Dictionary<Control, ItemsControl>();
        RenderedFrame.WhenDrawn(this, () => IsEffectivelyVisible && ShowsSessionRow(rowLists), time, at => {
            rowLists.Clear();
            painted(at);
        });
    }

    // The group's row list is looked up once per container and kept for later layout passes.
    bool ShowsSessionRow(Dictionary<Control, ItemsControl> rowLists) {
        if (DataContext is not MainWindowViewModel { Rail: { } rail }) return false;
        var viewport = new Rect(RailScroll.Viewport);
        for (var i = 0; i < rail.Groups.Count; i++) {
            if (rail.Groups[i] is not { IsExpanded: true, Sessions.Count: > 0 } || RailGroups.ContainerFromIndex(i) is not { } group) continue;
            if (!rowLists.TryGetValue(group, out var rows)) {
                if (group.FindDescendantOfType<ItemsControl>() is not { } found) continue;
                rowLists[group] = rows = found;
            }
            if (RowInside(rows, viewport)) return true;
        }
        return false;
    }

    // Rows stack top to bottom, so the first whose bottom is below the viewport's top is the only one
    // that can be inside it.
    bool RowInside(ItemsControl rows, Rect viewport) {
        // The container's child, not the container: the row's top margin is inside the container's bounds.
        Rect? Row(int index) => rows.ContainerFromIndex(index) is { } container
            && (container is ContentPresenter { Child: { } child } ? child : container) is var row
            && row.TranslatePoint(default, RailScroll) is { } at
            ? new Rect(at, row.Bounds.Size) : null;
        int low = 0, high = rows.ItemCount - 1, first = -1;
        while (low <= high) {
            var mid = (low + high) / 2;
            if (Row(mid) is not { } rect) return false;
            if (rect.Bottom > viewport.Top) {
                first = mid;
                high = mid - 1;
            } else low = mid + 1;
        }
        return first >= 0 && Row(first) is { Height: > 0 } candidate && candidate.Intersects(viewport);
    }

    // A pointer-opened flyout leaves focus on its button, outside the popup, where Escape never reaches it.
    static void KeyboardDismissible(Button anchor) {
        if (anchor.Flyout is not Flyout { Content: Control content } flyout) return;

        flyout.Opened += (_, _) => content.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })?.Focus(NavigationMethod.Tab);
        content.AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            flyout.Hide();
            anchor.Focus(NavigationMethod.Tab);
        }, RoutingStrategies.Bubble);
    }

    // The field shows the page's query while the page is up, and its placeholder otherwise.
    void BindSearchLabel() {
        if (DataContext is not MainWindowViewModel vm) {
            _searchLabel.Disposable = null;
            ShowSearchLabel(null);
            return;
        }
        var query = vm.Search is { } search ? search.WhenAnyValue(x => x.Query) : Observable.Return("");
        _searchLabel.Disposable = vm.WhenAnyValue(x => x.IsSearchOpen)
            .CombineLatest(query, (open, text) => open && text.Length > 0 ? text : null)
            .Subscribe(ShowSearchLabel);
    }

    void ShowSearchLabel(string? query) {
        RailSearchText.Text = query ?? "Search…";
        RailSearchText.Classes.Set("query", query is not null);
        RailSearchShortcut.IsVisible = query is null;
    }

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);


    // Button.OnClick raises ClickEvent before reading Command, and Flyout.Hide detaches the popup
    // parent and clears inherited DataContext on its rows, so capture and run Command here.
    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailHelpButton, sender, e);

    void OnGroupPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailGroupButton, sender, e);

    void OnSortPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailSortButton, sender, e);

    static void RunFlyoutItem(Button anchor, object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command, CommandParameter: var parameter }
            || !command.CanExecute(parameter)) return;
        anchor.Flyout?.Hide();
        command.Execute(parameter);
        e.Handled = true;
    }

    void OnPullRequestClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { DataContext: RailSessionViewModel { HasMultiplePullRequests: true, PullRequest: { } summary } vm } anchor)
            return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = new Flyout {
            Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 6,
            Content = new ScrollViewer { Content = rows, MaxHeight = 400, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        foreach (var item in summary.MenuItems) {
            var body = new StackPanel { Spacing = 3 };
            body.Children.Add(new TextBlock {
                Text = item.Label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (IBrush)this.FindResource("KcapTextBrush")!,
            });
            body.Children.Add(new TextBlock {
                Text = item.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (IBrush)this.FindResource("KcapMutedBrush")!,
            });
            var option = new Button {
                Content = body, DataContext = item, MinWidth = 240, MaxWidth = 360, Padding = new Thickness(10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            option.Classes.Add("kcapGhost");
            AutomationProperties.SetName(option, $"Open {item.Detail}: {item.Label}");
            ToolTip.SetTip(option, $"{item.Label}\n{item.Detail}");
            option.Click += (_, _) => {
                flyout.Hide();
                vm.OpenPullRequestChoiceCommand.Execute(item).Subscribe();
            };
            rows.Children.Add(option);
        }
        // Close when the access-aware summary changes, including expiry or removal.
        PropertyChangedEventHandler changed = (_, args) => {
            if (args.PropertyName == nameof(RailSessionViewModel.PullRequest)) flyout.Hide();
        };
        vm.PropertyChanged += changed;
        flyout.Closed += (_, _) => vm.PropertyChanged -= changed;
        flyout.Opened += (_, _) => rows.Children.OfType<Button>().FirstOrDefault()?.Focus();
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
        e.Handled = true;
    }
}
