using System.Collections.Specialized;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The search page drawn over the main area. DataContext is the window's MainWindowViewModel,
/// inherited. Focus never leaves the query box: the tabs and rows are not focusable, and the box
/// takes the page's keys before the window's navigation sees them.
public partial class SessionSearchView : UserControl {
    readonly SerialDisposable _focusRequests = new();
    Point? _pointerAt;
    ScrollViewer? _hitsScroll;

    public SessionSearchView() {
        InitializeComponent();
        SearchQueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        SearchHits.AddHandler(PointerWheelChangedEvent, OnHitsWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        SearchHits.TemplateApplied += (_, e) => _hitsScroll = e.NameScope.Find<ScrollViewer>("SearchHitsScroll");
        this.GetObservable(IsVisibleProperty).Subscribe(visible => {
            if (visible) Dispatcher.UIThread.Post(() => SearchQueryBox.Focus(), DispatcherPriority.Loaded);
        });
        DataContextChanged += (_, _) => _focusRequests.Disposable = Window?.SearchFocusRequests.Subscribe(_ => FocusAndSelect());
        // New results, or an unavailable hit's removal, move the highlight to the first hit but keep the old scroll
        // offset, which can leave that hit off screen for Enter to open.
        SearchHits.ItemsView.CollectionChanged += (_, e) => {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                Dispatcher.UIThread.Post(ScrollToHighlight, DispatcherPriority.Loaded);
        };
    }

    MainWindowViewModel? Window => DataContext as MainWindowViewModel;
    SessionSearchViewModel? Search => Window?.Search;

    void FocusAndSelect() {
        SearchQueryBox.Focus();
        SearchQueryBox.SelectAll();
    }

    void OnQueryKeyDown(object? sender, KeyEventArgs e) {
        if (Window is not { } window) return;
        var search = window.SearchSignedOut ? null : Search;
        switch (e.Key) {
            case Key.Escape:
                window.DismissSearchCommand.Execute().Subscribe();
                break;
            case Key.Up:
                MoveHighlight(search, -1);
                break;
            case Key.Down:
                MoveHighlight(search, 1);
                break;
            case Key.Enter:
                if (search is not null) _ = search.OpenHighlighted();
                break;
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                search?.PreviousTab();
                break;
            case Key.Tab:
                search?.NextTab();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // The list builds only the rows on screen, so a highlight moved past them has to be scrolled to.
    void MoveHighlight(SessionSearchViewModel? search, int delta) {
        if (search is null) return;
        search.MoveHighlight(delta);
        ScrollToHighlight();
    }

    void ScrollToHighlight() {
        if (Search is { } search && search.HighlightIndex >= 0 && search.HighlightIndex < search.Hits.Count)
            SearchHits.ScrollIntoView(search.HighlightIndex);
    }

    void OnSidebarTabClick(object? sender, RoutedEventArgs e) => SelectTab(SearchMode.Sidebar);

    void OnSessionsTabClick(object? sender, RoutedEventArgs e) => SelectTab(SearchMode.Sessions);

    void OnTranscriptsTabClick(object? sender, RoutedEventArgs e) => SelectTab(SearchMode.Transcripts);

    void SelectTab(SearchMode tab) {
        if (Search is { } search) search.Tab = tab;
        SearchQueryBox.Focus();
    }

    void OnHitClick(object? sender, RoutedEventArgs e) {
        if (sender is Button { DataContext: SearchHitViewModel hit } && Search is { } search) _ = search.OpenAsync(hit);
    }

    // Rows scrolled under a resting pointer raise pointer events too; only a pointer that moved over the list may
    // take the highlight from the keyboard.
    void OnHitPointerMoved(object? sender, PointerEventArgs e) {
        var at = e.GetPosition(SearchHits);
        if (at == _pointerAt) return;
        _pointerAt = at;
        if (sender is Button { DataContext: SearchHitViewModel hit }) Highlight(hit);
    }

    // A wheel scroll moves rows under a still pointer without a PointerMoved, which would leave the highlight off
    // screen for Enter. Once the list has moved, the row under the pointer takes it; with the pointer off every row,
    // as over a scrollbar beside them, the highlight only steps onto the nearest row on screen. A wheel that scrolls
    // nothing leaves it where the keyboard put it.
    void OnHitsWheel(object? sender, PointerWheelEventArgs e) {
        if (_hitsScroll is not { } scroll) return;
        var at = e.GetPosition(scroll);
        var from = scroll.Offset;
        Dispatcher.UIThread.Post(() => {
            if (scroll.Offset != from) HighlightAfterScroll(scroll, at);
        }, DispatcherPriority.Loaded);
    }

    void HighlightAfterScroll(ScrollViewer scroll, Point pointer) {
        if (Search is not { } search) return;
        // Rows are read where the new offset arranges them, not where the last layout pass left them.
        scroll.UpdateLayout();
        int under = -1, first = int.MaxValue, last = -1;
        foreach (var row in SearchHits.GetRealizedContainers()) {
            var index = SearchHits.IndexFromContainer(row);
            if (index < 0 || !row.IsVisible || row.TranslatePoint(default, scroll) is not { } top) continue;
            var bounds = new Rect(top, row.Bounds.Size);
            if (bounds.Contains(pointer)) under = index;
            if (bounds.Top < -0.5 || bounds.Bottom > scroll.Viewport.Height + 0.5) continue;
            first = Math.Min(first, index);
            last = Math.Max(last, index);
        }
        var target = under >= 0 ? under
            : last < 0 || (search.HighlightIndex >= first && search.HighlightIndex <= last) ? -1
            : search.HighlightIndex < first ? first : last;
        if (target >= 0 && target != search.HighlightIndex) search.MoveHighlight(target - search.HighlightIndex);
    }

    void Highlight(SearchHitViewModel hit) {
        if (Search is not { } search) return;
        var index = search.Hits.IndexOf(hit);
        if (index >= 0 && index != search.HighlightIndex) search.MoveHighlight(index - search.HighlightIndex);
    }

    // Only the strip above the page's panel stands in for the title bar.
    void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (ReferenceEquals(e.Source, SearchBackground) && e.GetPosition(this).Y < 20) WindowChrome.BeginDrag(this, e);
    }
}
