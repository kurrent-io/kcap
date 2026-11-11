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

    public SessionSearchView() {
        InitializeComponent();
        SearchQueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
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
        if (sender is not Button { DataContext: SearchHitViewModel hit } || Search is not { } search) return;
        var index = search.Hits.IndexOf(hit);
        if (index >= 0 && index != search.HighlightIndex) search.MoveHighlight(index - search.HighlightIndex);
    }

    // Only the strip above the page's panel stands in for the title bar.
    void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (ReferenceEquals(e.Source, SearchBackground) && e.GetPosition(this).Y < 20) WindowChrome.BeginDrag(this, e);
    }
}
