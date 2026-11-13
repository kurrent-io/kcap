using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Sizes a chat view's centred column as a share of the chat's width, and the agent's lane inside it
/// as a share of the column, from its window's <see cref="PaneLayout"/>. The edge handles drag the
/// column about the centre; the lane handle drags the lane's right edge. A drag commits on release,
/// so the store is written once per drag rather than on every pointer move.
public sealed class ChatColumnSizing {
    // Room for a handle beside the scrollbar once the pointer expands it.
    const double HandleReserve = 20;
    const double HandleWidth = 8;

    readonly ChatTabView _view;
    readonly ItemsControl _items;
    readonly Control _composerContent;
    readonly Control[] _handles;
    readonly Control _leftHandle;
    readonly Control _rightHandle;
    readonly Control _laneHandle;
    ChatTailPanel? _tail;
    PaneLayout? _panes;
    double _column = PaneLayout.ChatColumnDefaultFraction;
    double _lane = PaneLayout.ChatLaneDefaultFraction;
    IPointer? _dragPointer;
    Control? _dragHandle;
    double _dragStartX;
    double _dragStartWidth;

    ChatColumnSizing(ChatTabView view, ItemsControl items, Control composerContent,
            Control leftHandle, Control rightHandle, Control laneHandle) {
        _view = view;
        _items = items;
        _composerContent = composerContent;
        _leftHandle = leftHandle;
        _rightHandle = rightHandle;
        _laneHandle = laneHandle;
        _handles = [leftHandle, rightHandle, laneHandle];

        items.TemplateApplied += (_, e) => {
            _tail = e.NameScope.Find<ChatTailPanel>("ChatTail");
            Apply();
        };
        view.AttachedToVisualTree += (_, _) => {
            _panes = (TopLevel.GetTopLevel(view) as MainWindow)?.Panes;
            if (_panes is not null) {
                _panes.Changed += OnPanesChanged;
                (_column, _lane) = (_panes.ChatColumnFraction, _panes.ChatLaneFraction);
            }
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (_panes is not null) _panes.Changed -= OnPanesChanged;
            _panes = null;
        };
        items.SizeChanged += (_, _) => Apply();
        foreach (var handle in _handles) Wire(handle);
        Apply();
    }

    public static ChatColumnSizing Attach(ChatTabView view, ItemsControl items, Control composerContent,
            Control leftHandle, Control rightHandle, Control laneHandle) =>
        new(view, items, composerContent, leftHandle, rightHandle, laneHandle);

    /// For a view outside a <see cref="MainWindow"/>, which has no layout to read the shares from.
    internal void UseShares(double column, double lane) {
        (_column, _lane) = (column, lane);
        Apply();
    }

    void OnPanesChanged(object? sender, EventArgs e) {
        if (_dragPointer is not null || _panes is null) return;
        if (_panes.ChatColumnFraction == _column && _panes.ChatLaneFraction == _lane) return;
        (_column, _lane) = (_panes.ChatColumnFraction, _panes.ChatLaneFraction);
        Apply();
    }

    void Wire(Control handle) {
        handle.Width = HandleWidth;
        handle.PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount >= 2) {
                if (handle == _laneHandle) _lane = PaneLayout.ChatLaneDefaultFraction;
                else _column = PaneLayout.ChatColumnDefaultFraction;
                Commit();
                return;
            }
            _dragPointer = e.Pointer;
            _dragHandle = handle;
            _dragStartX = e.GetPosition(_items).X;
            _dragStartWidth = handle == _laneHandle ? LaneWidth(ColumnWidth()) : ColumnWidth();
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) => {
            if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
            var dx = e.GetPosition(_items).X - _dragStartX;
            if (handle == _laneHandle) _lane = LaneFractionOf(_dragStartWidth + dx, ColumnWidth());
            else _column = ColumnFractionOf(_dragStartWidth + (handle == _leftHandle ? -2 : 2) * dx);
            Apply();
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) => {
            if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
            EndDrag();
            e.Pointer.Capture(null);
            Commit();
            e.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => {
            if (_dragHandle != handle) return;
            EndDrag();
            Commit();
        };
    }

    void EndDrag() {
        _dragPointer = null;
        _dragHandle = null;
    }

    double MaxColumnWidth => Math.Max(0, _items.Bounds.Width - 2 * HandleReserve);

    double MessageMinWidth => Math.Max(0, Math.Min(PaneLayout.ChatMessageMinWidth,
        _items.Bounds.Width - PaneLayout.ChatMessageGutters));

    double ColumnWidth() => Math.Min(Math.Max(_column * _items.Bounds.Width, MessageMinWidth), MaxColumnWidth);

    // A drag to the widest the chat allows stays the widest on any later window size.
    double ColumnFractionOf(double width) => width >= MaxColumnWidth || _items.Bounds.Width <= 0
        ? 1
        : Math.Max(MessageMinWidth, width) / _items.Bounds.Width;

    double LaneWidth(double column) => Math.Min(Math.Max(_lane * column, MessageMinWidth), column);

    double LaneFractionOf(double width, double column) => column <= 0 || width >= column
        ? 1
        : Math.Max(MessageMinWidth, width) / column;

    void Commit() {
        if (_panes is not null) {
            _panes.ChatColumnFraction = _column;
            _panes.ChatLaneFraction = _lane;
        }
        Apply();
    }

    void Apply() {
        // The edge handles sit in a strip just outside the column: the window's right edge belongs
        // to the scrollbar.
        var available = _items.Bounds.Width;
        var column = ColumnWidth();
        var lane = LaneWidth(column);
        if (_tail is not null) _tail.ColumnWidth = column;
        _view.LaneWidth = lane;
        _composerContent.MaxWidth = column;

        foreach (var handle in _handles) handle.IsVisible = column > 0;
        var left = (available - column) / 2;
        _leftHandle.Margin = new Thickness(left - HandleWidth, 0, 0, 0);
        _rightHandle.Margin = new Thickness(left + column, 0, 0, 0);
        // A full-width lane's edge is the column's: its handle steps inside, clear of the column's.
        _laneHandle.Margin = new Thickness(left + Math.Min(lane, column - HandleWidth), 0, 0, 0);
    }
}
