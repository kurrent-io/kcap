using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Dragging a list's rows, the controls carrying `rowClass`, onto drop zones the owner names. A press that moves a few
/// pixels becomes a drag, and Escape cancels it. The host captures the pointer, so the row's button never reads the drop's
/// release as a click. A press the owner already handled starts nothing. While dragging, the dragged rows carry `lifted`,
/// and the target under the pointer shows as `dropOver` on its zone or as the insertion line at its edge.
public sealed class RowDrag {
    public const string LiftedClass = "lifted";
    public const string DropOverClass = "dropOver";
    const double StartDistance = 4;

    readonly Control _host;
    readonly Control _list;
    readonly Control _ghost;
    readonly Control? _line;
    readonly string _rowClass;
    readonly Func<object, object?> _pick;
    readonly Func<object, Point, RowDropTarget?> _targetAt;
    readonly Action<object>? _started;
    readonly Action? _ended;
    readonly Action? _pressReleased;
    // Moved by transform, not Canvas.Left/Top: a layout pass per pointer move runs every LayoutUpdated handler in the window.
    readonly TranslateTransform _ghostAt = new();
    readonly TranslateTransform _lineAt = new();
    object? _pressed;
    Point _pressAt;
    object? _dragged;
    IPointer? _pointer;
    List<Control> _lifted = [];
    RowDropTarget? _over;

    /// <param name="host">Captures the pointer while dragging; the ghost's Canvas covers it.</param>
    /// <param name="ghost">Follows the pointer while dragging; it and <paramref name="line"/> sit at the origin of a Canvas
    /// over the host.</param>
    /// <param name="pick">What a drag from a row carries, from the row's data; null when that row can't be dragged now.
    /// A row whose data is what it carries, or is in it when that is a list, is lifted.</param>
    /// <param name="targetAt">The drop zone at a point in the host for what the drag carries, or null.</param>
    /// <param name="started">A drag started carrying this, to label the ghost.</param>
    /// <param name="pressReleased">A press on a row ended without becoming a drag.</param>
    /// <param name="line">Marks a drop at a zone's edge, spanning the zone; needed only when a target names an edge.</param>
    public RowDrag(Control host, Control list, Control ghost, string rowClass, Func<object, object?> pick,
            Func<object, Point, RowDropTarget?> targetAt, Action<object>? started = null, Action? ended = null, Action? pressReleased = null,
            Control? line = null) {
        _host = host;
        _list = list;
        _ghost = ghost;
        _line = line;
        _rowClass = rowClass;
        _pick = pick;
        _targetAt = targetAt;
        _started = started;
        _ended = ended;
        _pressReleased = pressReleased;
        ghost.RenderTransform = _ghostAt;
        if (line is not null) line.RenderTransform = _lineAt;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        host.PointerCaptureLost += (_, _) => End();
    }

    Control? RowOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains(_rowClass));

    void OnPressed(object? sender, PointerPressedEventArgs e) {
        _pressed = e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed ? RowOf(e.Source)?.DataContext : null;
        _pressAt = e.GetPosition(_host);
    }

    void OnMoved(object? sender, PointerEventArgs e) {
        var at = e.GetPosition(_host);
        if (_dragged is null) {
            if (_pressed is not { } pressed) return;
            if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) {
                _pressed = null;
                return;
            }
            if (Math.Abs(at.X - _pressAt.X) < StartDistance && Math.Abs(at.Y - _pressAt.Y) < StartDistance) return;
            if (_pick(pressed) is not { } carried) return;
            Start(carried, e.Pointer);
        }
        _ghostAt.X = at.X - 14;
        _ghostAt.Y = at.Y - 16;
        Show(_targetAt(_dragged!, at));
        e.Handled = true;
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e) {
        if (_dragged is null) {
            if (_pressed is null) return;
            _pressed = null;
            _pressReleased?.Invoke();
            return;
        }
        var drop = _targetAt(_dragged, e.GetPosition(_host))?.Drop;
        End();
        drop?.Invoke();
        e.Handled = true;
    }

    void OnKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || _dragged is null) return;
        End();
        e.Handled = true;
    }

    void Start(object carried, IPointer pointer) {
        _pressed = null;
        _dragged = carried;
        _pointer = pointer;
        pointer.Capture(_host);
        _started?.Invoke(carried);
        _ghost.IsVisible = true;
        _ = Motion.Standard.RunAsync(_ghost, CancellationToken.None, (Visual.OpacityProperty, 0d, 1d));
        _lifted = [.. _list.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains(_rowClass) && c.DataContext is { } data && Carries(carried, data))];
        foreach (var row in _lifted) row.Classes.Add(LiftedClass);
    }

    static bool Carries(object carried, object data) =>
        carried is IEnumerable many and not string ? many.Cast<object>().Contains(data) : Equals(carried, data);

    void End() {
        _pressed = null;
        if (_dragged is null) return;
        _dragged = null;
        _ghost.IsVisible = false;
        foreach (var row in _lifted) row.Classes.Remove(LiftedClass);
        _lifted = [];
        Show(null);
        _ended?.Invoke();
        _pointer?.Capture(null);
        _pointer = null;
    }

    // An edge is drawn at its y, so the bottom of one row and the top of the next show the same line.
    void Show(RowDropTarget? target) {
        if (_over?.Zone != target?.Zone || _over?.Edge != target?.Edge) {
            if (_over is { Edge: RowDropEdge.Over }) _over.Zone.Classes.Remove(DropOverClass);
            if (target is { Edge: RowDropEdge.Over }) target.Zone.Classes.Add(DropOverClass);
        }
        _over = target;
        if (_line is null) return;
        if (target is not { Edge: not RowDropEdge.Over } edge
            || edge.Zone.TranslatePoint(new Point(0, edge.Edge == RowDropEdge.Top ? 0 : edge.Zone.Bounds.Height), _host) is not { } at) {
            _line.IsVisible = false;
            return;
        }
        _lineAt.X = at.X;
        _lineAt.Y = at.Y - _line.Height / 2;
        _line.Width = edge.Zone.Bounds.Width;
        _line.IsVisible = true;
    }
}
