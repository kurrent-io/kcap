using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

internal sealed partial class ChatTurnNavigator : UserControl {
    internal const double GutterWidth = 28;
    readonly ChatTurnIndex _index = new();
    readonly AvaloniaList<ChatTurnOutlineEntry> _outline = [];
    ChatTabView? _view;
    ChatTabViewModel? _chat;
    ScrollViewer? _scroll;
    TopLevel? _outlineRoot;
    DispatcherOperation? _refresh;
    DispatcherOperation? _checkFocus;
    Point? _activationPoint;
    bool _rebuild;
    bool _hasEarlier;
    int _outlineStarts;

    public ChatTurnNavigator() {
        InitializeComponent();
        IsVisible = false;
        TurnMap.Index = _index;
        TurnMapButton.Click += (_, _) => ToggleOutline();
        TurnMapButton.ChangeOutline = open => {
            if (open) OpenOutline(at: -1);
            else CloseOutline(restoreFocus: true);
        };
        TurnMapButton.AddHandler(PointerPressedEvent, (_, e) => {
            if (e.GetCurrentPoint(TurnMapButton).Properties.IsLeftButtonPressed) _activationPoint = e.GetPosition(TurnMap);
        }, RoutingStrategies.Tunnel);
        TurnMapButton.PointerEntered += (_, _) => Widen(true);
        TurnMapButton.PointerExited += (_, _) => Widen(false);
        TurnMapButton.PointerWheelChanged += (_, e) => _view?.ScrollFromNavigator(e);
        TurnMapButton.AddHandler(ScrollGestureEvent, (_, e) => _view?.ScrollFromNavigator(e));
        TurnMapButton.AddHandler(KeyDownEvent, (_, e) => {
            _activationPoint = null;
            if (TurnOutline.IsVisible) OnOutlineKey(TurnMapButton, e);
        }, RoutingStrategies.Tunnel);
        TurnList.ItemsSource = _outline;
        TurnList.AddHandler(KeyDownEvent, OnOutlineKey, RoutingStrategies.Tunnel);
        TurnList.Activate = Activate;
        TurnList.AddHandler(TappedEvent, OnOutlineTap, RoutingStrategies.Bubble, handledEventsToo: true);
        KeyboardNavigation.SetTabNavigation(TurnList, KeyboardNavigationMode.Once);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    internal ChatTabViewModel? Chat => _chat;
    internal ItemsControl? Transcript => _view?.NavigatorTranscript;
    internal bool HasFocus => IsKeyboardFocusWithin;

    internal void Start(ChatTabView view, ChatTabViewModel chat, ScrollViewer scroll) {
        _view = view;
        _chat = chat;
        _scroll = scroll;
        TurnMap.Scroll = scroll;
        chat.Items.CollectionChanged += OnItemsChanged;
        chat.NavigatorHistoryChanged += OnHistoryChanged;
        scroll.ScrollChanged += OnScrollChanged;
        QueueRefresh();
    }

    internal void Stop() {
        _checkFocus?.Abort();
        _checkFocus = null;
        _refresh?.Abort();
        _refresh = null;
        if (_chat is { } chat) {
            chat.Items.CollectionChanged -= OnItemsChanged;
            chat.NavigatorHistoryChanged -= OnHistoryChanged;
        }
        if (_scroll is { } scroll) scroll.ScrollChanged -= OnScrollChanged;
        CloseOutline(restoreFocus: false);
        _index.Reset();
        _chat = null;
        _scroll = null;
        _view = null;
        TurnMap.Panel = null;
        TurnMap.Scroll = null;
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewStartingIndex < _index.IndexedRows) _rebuild = true;
        if (e.Action == NotifyCollectionChangedAction.Reset) Withdraw();
        if (!_rebuild && _refresh is null && IsVisible && !TurnOutline.IsVisible
            && _chat is { CanNavigateTranscript: true } chat && chat.HasEarlierTurns == _hasEarlier) {
            _index.Append(chat.Items, _hasEarlier);
            TurnMap.InvalidateVisual();
            return;
        }
        QueueRefresh();
    }

    void OnHistoryChanged() {
        if (_chat?.CanNavigateTranscript != true) Withdraw();
        QueueRefresh();
    }

    void Withdraw() {
        if (HasFocus) _view?.FocusAfterNavigator();
        CloseOutline(restoreFocus: false);
        _index.Reset();
        SetAvailable(false);
    }

    void SetAvailable(bool available) {
        if (IsVisible == available) return;
        IsVisible = available;
        _view?.ReserveNavigatorSpace(available);
    }

    void QueueRefresh() => _refresh ??= Dispatcher.UIThread.InvokeAsync(Refresh, DispatcherPriority.Background);

    void Refresh() {
        _refresh = null;
        if (_chat is not { CanNavigateTranscript: true } chat || _view is null) {
            Withdraw();
            return;
        }
        var earlier = chat.HasEarlierTurns;
        var rebuild = _rebuild || earlier != _hasEarlier;
        _hasEarlier = earlier;
        _rebuild = false;
        if (rebuild) _index.Reset();
        _index.Append(chat.Items, earlier);
        TurnMap.Panel = _view.NavigatorPanel;
        var available = _index.Starts.Count > 0 || earlier;
        if (!available) Withdraw();
        SetAvailable(available);
        if (TurnOutline.IsVisible) UpdateOutline(rebuild);
        TurnMap.InvalidateVisual();
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => TurnMap.InvalidateVisual();

    void Widen(bool wide) {
        if (!wide) _activationPoint = null;
        TurnMap.IsWide = wide;
        TurnMap.InvalidateVisual();
    }

    void ToggleOutline() {
        var at = _activationPoint is { } point ? TurnMap.DestinationAt(point) : -1;
        _activationPoint = null;
        if (TurnOutline.IsVisible) {
            CloseOutline(restoreFocus: true);
            return;
        }
        OpenOutline(at);
    }

    void OpenOutline(int at) {
        if (TurnOutline.IsVisible || !IsEffectivelyVisible || _chat?.CanNavigateTranscript != true) return;
        TurnOutline.IsVisible = true;
        TurnMapButton.SetOutlineOpen(true);
        _outlineRoot = TopLevel.GetTopLevel(this);
        _outlineRoot?.AddHandler(PointerPressedEvent, OnOutsidePress, RoutingStrategies.Tunnel);
        if (_outlineRoot is Window window) window.Deactivated += OnOutlineDeactivated;
        TurnMapButton.IsTabStop = false;
        UpdateOutline(rebuild: true);
        TurnList.Focus(NavigationMethod.Tab);
        var target = at < 0 ? 0 : _outline.Count - _outlineStarts + at;
        Select(target);
    }

    void UpdateOutline(bool rebuild) {
        var selected = TurnList.SelectedItem as ChatTurnOutlineEntry;
        var refocus = TurnList.IsKeyboardFocusWithin;
        if (rebuild || _outlineStarts == 0 && _index.Starts.Count > 0) {
            _outline.Clear();
            if (_index.Starts.Count > 0) {
                _outline.Add(new(null, ChatTurnOutlineEntry.Action.First));
                _outline.Add(new(null, ChatTurnOutlineEntry.Action.Latest));
            }
            if (_hasEarlier) _outline.Add(new(null, ChatTurnOutlineEntry.Action.LoadEarlier));
            _outlineStarts = 0;
        }
        if (_outlineStarts < _index.Starts.Count) {
            _outline.AddRange(_index.Starts.Skip(_outlineStarts).Select(destination => new ChatTurnOutlineEntry(destination)));
            _outlineStarts = _index.Starts.Count;
        }
        var count = _index.Starts.Count;
        var history = !_hasEarlier ? "Start of available history"
            : _chat?.IsLoadingEarlierTurns == true ? "Loading earlier history…" : "Earlier history is available";
        var status = string.Create(CultureInfo.CurrentCulture, $"{count:N0} loaded entries · {history}");
        if (HistoryStatus.Text != status) HistoryStatus.Text = status;
        if (rebuild && refocus) {
            var index = selected is null ? 0 : _outline.IndexOf(selected);
            if (index < 0 && selected?.Destination is { } destination) {
                for (var i = 0; i < _outline.Count; i++) {
                    if (!ReferenceEquals(_outline[i].Destination?.Row, destination.Row)
                        && (destination.Row.SelectionKey is not { } key || _outline[i].Destination?.Row.SelectionKey != key)) continue;
                    index = i;
                    break;
                }
            }
            Select(Math.Max(0, index));
        }
    }

    void Select(int index) {
        if (_outline.Count == 0) return;
        index = Math.Clamp(index, 0, _outline.Count - 1);
        TurnList.SelectedIndex = index;
        TurnList.ScrollIntoView(index);
        TurnList.UpdateLayout();
        if (TurnList.ContainerFromIndex(index) is not Control item || !item.Focus(NavigationMethod.Directional))
            TurnList.Focus(NavigationMethod.Tab);
    }

    void OnOutlineKey(object? sender, KeyEventArgs e) {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var index = TurnList.SelectedIndex;
        switch (e.Key) {
            case Key.Up: Select(index - 1); break;
            case Key.Down: Select(index + 1); break;
            case Key.Home: Select(_outline.Count - _outlineStarts); break;
            case Key.End: Select(_outline.Count - 1); break;
            case Key.Escape: CloseOutline(restoreFocus: true); break;
            case Key.Enter:
            case Key.Space:
                if (TurnList.SelectedItem is ChatTurnOutlineEntry entry) Activate(entry);
                break;
            default: return;
        }
        e.Handled = true;
    }

    void OnOutlineTap(object? sender, TappedEventArgs e) {
        if (e.Source is not Visual source
            || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { DataContext: ChatTurnOutlineEntry entry }) return;
        Activate(entry);
        e.Handled = true;
    }

    void Activate(ChatTurnOutlineEntry entry) {
        if (!TurnOutline.IsVisible || _chat is not { CanNavigateTranscript: true } chat) return;
        if (entry.Command == ChatTurnOutlineEntry.Action.LoadEarlier) {
            if (chat.HasEarlierTurns && chat.RequestOlder()) {
                UpdateOutline(rebuild: false);
                LiveAnnouncer.Polite(this, "Loading earlier history");
            }
            return;
        }
        var row = entry.Command switch {
            ChatTurnOutlineEntry.Action.First => _index.First.Row,
            ChatTurnOutlineEntry.Action.Latest => _index.Latest.Row,
            _ => entry.Destination!.Row,
        };
        CloseOutline(restoreFocus: true);
        _view?.JumpFromNavigator(chat, row);
    }

    void OnLostFocus(object? sender, FocusChangedEventArgs e) {
        if (!TurnOutline.IsVisible) return;
        _checkFocus ??= Dispatcher.UIThread.InvokeAsync(() => {
            _checkFocus = null;
            if (!IsKeyboardFocusWithin) CloseOutline(restoreFocus: false);
        }, DispatcherPriority.Input);
    }

    void OnOutsidePress(object? sender, PointerPressedEventArgs e) {
        if (e.Source is not Visual source || !this.IsVisualAncestorOf(source)) CloseOutline(restoreFocus: false);
    }

    void OnOutlineDeactivated(object? sender, EventArgs e) => CloseOutline(restoreFocus: false);

    void CloseOutline(bool restoreFocus) {
        if (!TurnOutline.IsVisible) return;
        if (_outlineRoot is Window window) window.Deactivated -= OnOutlineDeactivated;
        _outlineRoot?.RemoveHandler(PointerPressedEvent, OnOutsidePress);
        _outlineRoot = null;
        TurnMapButton.IsTabStop = true;
        TurnMapButton.SetOutlineOpen(false);
        if (restoreFocus) TurnMapButton.Focus(NavigationMethod.Tab);
        TurnOutline.IsVisible = false;
        _outline.Clear();
        _outlineStarts = 0;
    }
}
