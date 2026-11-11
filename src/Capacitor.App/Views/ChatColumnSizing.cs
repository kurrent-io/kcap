using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Sizes a chat view's centred column from its window's <see cref="PaneLayout"/> and lets either edge
/// handle drag it wider or narrower about the centre. A drag commits on release, so the store is
/// written once per drag rather than on every pointer move.
public sealed class ChatColumnSizing {
    // The transcript's side margin inside the column; the composer band's padding matches it.
    const double Gutter = 22;
    // Room for a handle beside the scrollbar once the pointer expands it.
    const double HandleReserve = 20;
    const double HandleWidth = 8;

    readonly Panel _area;
    readonly Control _composerContent;
    readonly Control _leftHandle;
    readonly Control _rightHandle;
    ChatTailPanel? _tail;
    PaneLayout? _panes;
    double _width = PaneLayout.ChatColumnDefaultWidth;
    IPointer? _dragPointer;
    double _dragStartX;
    double _dragStartWidth;
    int _dragSign;

    ChatColumnSizing(Control view, ItemsControl items, Panel area, Control composerContent, Control leftHandle, Control rightHandle) {
        _area = area;
        leftHandle.Width = HandleWidth;
        rightHandle.Width = HandleWidth;
        _composerContent = composerContent;
        _leftHandle = leftHandle;
        _rightHandle = rightHandle;

        items.TemplateApplied += (_, e) => {
            _tail = e.NameScope.Find<ChatTailPanel>("ChatTail");
            Apply();
        };
        view.AttachedToVisualTree += (_, _) => {
            _panes = (TopLevel.GetTopLevel(view) as MainWindow)?.Panes;
            if (_panes is not null) {
                _panes.Changed += OnPanesChanged;
                _width = _panes.ChatColumnWidth;
            }
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (_panes is not null) _panes.Changed -= OnPanesChanged;
            _panes = null;
        };
        area.SizeChanged += (_, _) => Apply();
        Wire(leftHandle, -1);
        Wire(rightHandle, 1);
        Apply();
    }

    public static ChatColumnSizing Attach(Control view, ItemsControl items, Panel area, Control composerContent,
            Control leftHandle, Control rightHandle) =>
        new(view, items, area, composerContent, leftHandle, rightHandle);

    void OnPanesChanged(object? sender, EventArgs e) {
        if (_dragPointer is not null || _panes is null || _panes.ChatColumnWidth == _width) return;
        _width = _panes.ChatColumnWidth;
        Apply();
    }

    void Wire(Control handle, int sign) {
        handle.PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) {
                Commit(PaneLayout.ChatColumnDefaultWidth);
                e.Handled = true;
                return;
            }
            _dragPointer = e.Pointer;
            _dragStartX = e.GetPosition(_area).X;
            _dragStartWidth = Clamp(_width);
            _dragSign = sign;
            e.Pointer.Capture(handle);
            handle.Classes.Add("dragging");
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) => {
            if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
            var grown = (e.GetPosition(_area).X - _dragStartX) * 2 * _dragSign;
            _width = Clamp(_dragStartWidth + grown);
            Apply();
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) => {
            if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
            EndDrag(handle);
            e.Pointer.Capture(null);
            Commit(_width);
            e.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => {
            if (_dragPointer is null || _dragSign != sign) return;
            EndDrag(handle);
            Commit(_width);
        };
    }

    void EndDrag(Control handle) {
        _dragPointer = null;
        handle.Classes.Remove("dragging");
    }

    double Clamp(double width) =>
        Math.Max(PaneLayout.ChatColumnMinWidth, Math.Min(width, Math.Max(PaneLayout.ChatColumnMinWidth, _area.Bounds.Width - 2 * HandleReserve)));

    void Commit(double width) {
        _width = width;
        if (_panes is not null) _panes.ChatColumnWidth = width;
        Apply();
    }

    void Apply() {
        // Each handle sits in a strip just outside its edge: the gutter inside belongs to a margin drag
        // that selects text, and the window's right edge to the scrollbar. A window too narrow for
        // the strips gives the column all its width and shows no handles.
        var available = _area.Bounds.Width;
        var resizable = available - 2 * HandleReserve >= PaneLayout.ChatColumnMinWidth;
        var column = resizable ? Math.Min(_width, available - 2 * HandleReserve) : available;
        if (_tail is not null) _tail.ColumnWidth = column;
        _composerContent.MaxWidth = Math.Max(0, column - 2 * Gutter);

        _leftHandle.IsVisible = resizable;
        _rightHandle.IsVisible = resizable;
        var left = (available - column) / 2;
        _leftHandle.Margin = new Thickness(left - HandleWidth, 0, 0, 0);
        _rightHandle.Margin = new Thickness(left + column, 0, 0, 0);
    }
}
