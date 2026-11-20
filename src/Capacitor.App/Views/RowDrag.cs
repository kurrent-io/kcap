using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Dragging a list's rows, the controls carrying `rowClass`, onto drop zones the owner names. A press that moves a few
/// pixels becomes a drag, and Escape cancels it. The host captures the pointer, so the row's button never reads the drop's
/// release as a click. A press the owner already handled starts nothing. While dragging, the dragged rows carry `lifted`,
/// and the target under the pointer shows as `dropOver` on its zone or as the insertion line at its edge. The ghost shows a
/// snapshot of the grabbed row, held where it was grabbed; on release it glides onto the row where what it carried landed,
/// or back to where it came from, and fades into it. Row tooltips stay shut while dragging.
public sealed class RowDrag {
    public const string LiftedClass = "lifted";
    public const string DropOverClass = "dropOver";
    const double StartDistance = 4;

    readonly Control _host;
    readonly Control _list;
    readonly Control _ghost;
    readonly Image _snapshot;
    readonly Control? _line;
    readonly string _rowClass;
    readonly Func<object, object?> _pick;
    readonly Func<object, Point, RowDropTarget?> _targetAt;
    readonly Func<object, object> _keyOf;
    readonly Action<object>? _started;
    readonly Action? _ended;
    readonly Action? _pressReleased;
    readonly double? _compactGhostWidth;
    // Moved by transform, not Canvas.Left/Top: a layout pass per pointer move runs every LayoutUpdated handler in the window.
    readonly TranslateTransform _ghostAt = new();
    readonly TranslateTransform _lineAt = new();
    Control? _pressed;
    Point _pressAt;
    Vector _grab;
    object? _dragged;
    IPointer? _pointer;
    List<Control> _lifted = [];
    RowDropTarget? _over;
    CancellationTokenSource? _settling;
    List<Control> _landing = [];

    /// <param name="host">Captures the pointer while dragging; the ghost's Canvas covers it.</param>
    /// <param name="ghost">Follows the pointer while dragging, sized to the grabbed row; it and <paramref name="line"/> sit at
    /// the origin of a Canvas over the host.</param>
    /// <param name="snapshot">Inside the ghost, shows the grabbed row as it looked when the drag started.</param>
    /// <param name="pick">What a drag from a row carries, from the row's data; null when that row can't be dragged now.
    /// A row whose data is what it carries, or is in it when that is a list, is lifted.</param>
    /// <param name="targetAt">The drop zone at a point in the host for what the drag carries, or null.</param>
    /// <param name="started">A drag started carrying this, to label the ghost.</param>
    /// <param name="pressReleased">A press on a row ended without becoming a drag.</param>
    /// <param name="line">Marks a drop at a zone's edge, spanning the zone; needed only when a target names an edge.</param>
    /// <param name="keyOf">Identifies a row's data across a rebuild, so the ghost finds the row a drop landed on.</param>
    /// <param name="compactGhostWidth">Cuts the ghost to this width and carries it just right of the pointer, rather than
    /// holding the whole row where it was grabbed.</param>
    public RowDrag(Control host, Control list, Control ghost, Image snapshot, string rowClass, Func<object, object?> pick,
            Func<object, Point, RowDropTarget?> targetAt, Action<object>? started = null, Action? ended = null, Action? pressReleased = null,
            Control? line = null, Func<object, object>? keyOf = null, double? compactGhostWidth = null) {
        _host = host;
        _list = list;
        _ghost = ghost;
        _snapshot = snapshot;
        _line = line;
        _rowClass = rowClass;
        _pick = pick;
        _targetAt = targetAt;
        _started = started;
        _ended = ended;
        _pressReleased = pressReleased;
        _keyOf = keyOf ?? (data => data);
        _compactGhostWidth = compactGhostWidth;
        ghost.RenderTransform = _ghostAt;
        if (line is not null) line.RenderTransform = _lineAt;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        host.PointerCaptureLost += (_, _) => Finish(null);
    }

    Control? RowOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains(_rowClass));

    void OnPressed(object? sender, PointerPressedEventArgs e) {
        _pressed = e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed ? RowOf(e.Source) : null;
        _pressAt = e.GetPosition(_host);
    }

    void OnMoved(object? sender, PointerEventArgs e) {
        var at = e.GetPosition(_host);
        if (_dragged is null) {
            if (_pressed is not { DataContext: { } data } row) return;
            if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) {
                _pressed = null;
                return;
            }
            if (Math.Abs(at.X - _pressAt.X) < StartDistance && Math.Abs(at.Y - _pressAt.Y) < StartDistance) return;
            if (_pick(data) is not { } carried) return;
            Start(carried, row, e.Pointer);
        }
        _ghostAt.X = at.X - _grab.X;
        _ghostAt.Y = at.Y - _grab.Y;
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
        Finish(_targetAt(_dragged, e.GetPosition(_host))?.Drop);
        e.Handled = true;
    }

    void OnKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || _dragged is null) return;
        Finish(null);
        e.Handled = true;
    }

    void Start(object carried, Control row, IPointer pointer) {
        StopSettling();
        _pressed = null;
        _dragged = carried;
        _pointer = pointer;
        pointer.Capture(_host);
        ToolTip.SetServiceEnabled(_host, false);
        Snap(row);
        _started?.Invoke(carried);
        _ghost.IsVisible = true;
        _ghost.Opacity = 1;
        _ = Motion.Standard.RunAsync(_ghost, CancellationToken.None, (Visual.OpacityProperty, 0d, 1d));
        _lifted = RowsCarrying(carried);
        foreach (var lifted in _lifted) lifted.Classes.Add(LiftedClass);
    }

    // Before the row is lifted, so the snapshot is not dimmed.
    void Snap(Control row) {
        var scale = TopLevel.GetTopLevel(row)?.RenderScaling ?? 1;
        var bitmap = new RenderTargetBitmap(PixelSize.FromSize(row.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
        bitmap.Render(row);
        var stale = _snapshot.Source as IDisposable;
        _snapshot.Source = bitmap;
        stale?.Dispose();
        _ghost.Width = Math.Min(row.Bounds.Width, _compactGhostWidth ?? double.PositiveInfinity);
        _ghost.Height = row.Bounds.Height;
        _grab = _compactGhostWidth is null ? _pressAt - (row.TranslatePoint(default, _host) ?? default) : new Vector(-12, row.Bounds.Height / 2);
    }

    List<Control> RowsCarrying(object carried) {
        var keys = (carried is IEnumerable many and not string ? many.Cast<object>() : [carried]).Select(_keyOf).ToHashSet();
        return [.. _list.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains(_rowClass) && c.DataContext is { } data && keys.Contains(_keyOf(data)))];
    }

    void Finish(Action? drop) {
        _pressed = null;
        if (_dragged is not { } carried) return;
        var lifted = _lifted;
        _dragged = null;
        _lifted = [];
        Show(null);
        ToolTip.SetServiceEnabled(_host, true);
        _ended?.Invoke();
        _pointer?.Capture(null);
        _pointer = null;
        drop?.Invoke();
        _settling = new CancellationTokenSource();
        _ = SettleAsync(carried, lifted, drop is not null, _settling.Token);
    }

    async Task SettleAsync(object carried, List<Control> lifted, bool dropped, CancellationToken token) {
        _landing = lifted;
        if (dropped) {
            // A drop rebuilds the rows; ContextIdle runs once the rebuild's posted passes and layout have drained.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, CancellationToken.None);
            if (token.IsCancellationRequested) return;
            foreach (var row in lifted) row.Classes.Remove(LiftedClass);
            _landing = RowsCarrying(carried);
            foreach (var row in _landing) row.Classes.Add(LiftedClass);
        }
        if (_landing.FirstOrDefault(row => row.IsEffectivelyVisible) is { } onto
            && onto.TranslatePoint(default, _host) is { } at
            && new Rect(_host.Bounds.Size).Contains(at)) {
            var (fromX, fromY) = (_ghostAt.X, _ghostAt.Y);
            (_ghostAt.X, _ghostAt.Y) = (at.X, at.Y);
            await Motion.Standard.RunAsync(_ghost, token, (TranslateTransform.XProperty, fromX, at.X), (TranslateTransform.YProperty, fromY, at.Y));
            if (token.IsCancellationRequested) return;
        }
        foreach (var row in _landing) row.Classes.Remove(LiftedClass);
        _landing = [];
        _ghost.Opacity = 0;
        await Motion.Standard.RunAsync(_ghost, token, (Visual.OpacityProperty, 1d, 0d));
        if (!token.IsCancellationRequested) _ghost.IsVisible = false;
    }

    void StopSettling() {
        _settling?.Cancel();
        _settling?.Dispose();
        _settling = null;
        foreach (var row in _landing) row.Classes.Remove(LiftedClass);
        _landing = [];
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
