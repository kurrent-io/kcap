using System.ComponentModel;
using System.Globalization;
using System.Text;
using Capacitor.App.ProgressiveDisclosure;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

internal sealed class ChatSelection(IReadOnlyList<ChatItemViewModel> rows, Func<object?> currentFeed) {
    internal readonly record struct Point(ChatItemViewModel Row, ChatMessageKey Key, string Text, int Offset, int IndexHint) {
        public static Point At(ChatItemViewModel row, string text, int offset, int index = -1) =>
            new(row, row.SelectionKey!.Value, text, Boundary(text, offset), index);
    }

    object? _feed;
    ProgressiveDisclosureBasis? _basis;
    Point? _anchor;
    Point? _focus;
    bool _endpointTextDirty;

    public AgentLinkLabels? Labels { get; set; }
    public event Action? Changed;
    public event Action? EndpointTextChanged;
    public int Revision { get; private set; }
    public object? CurrentFeed => currentFeed();
    public bool HasRange => _anchor is { } anchor && _focus is { } focus
        && (anchor.Key != focus.Key || anchor.Offset != focus.Offset);
    public Point? Focus => _focus;
    public long LatestLine {
        get {
            for (var i = rows.Count - 1; i >= 0; i--)
                if (rows[i].SelectionKey is { } key) return key.Line;
            return long.MinValue;
        }
    }

    public static int Boundary(string text, int offset) {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset == text.Length) return offset;
        var starts = StringInfo.ParseCombiningCharacters(text);
        var index = Array.BinarySearch(starts, offset);
        return index >= 0 ? offset : starts[~index - 1];
    }

    public void Set(Point anchor, Point focus) {
        if (currentFeed() is not { } feed) {
            Clear();
            return;
        }
        if (_anchor == anchor && _focus == focus && ReferenceEquals(_feed, feed)) return;
        _feed = feed;
        _basis = ProgressiveDisclosureBasis.Current;
        var rowsChanged = !ReferenceEquals(_anchor?.Row, anchor.Row) || !ReferenceEquals(_focus?.Row, focus.Row);
        if (rowsChanged) ObserveEndpoints(observe: false);
        _endpointTextDirty |= rowsChanged || _anchor?.Text != anchor.Text || _focus?.Text != focus.Text;
        _anchor = anchor;
        _focus = focus;
        if (rowsChanged) ObserveEndpoints(observe: true);
        Revision++;
        Changed?.Invoke();
    }

    public void Clear() {
        if (_anchor is null) return;
        _feed = null;
        _basis = null;
        ObserveEndpoints(observe: false);
        _anchor = _focus = null;
        Revision++;
        Changed?.Invoke();
    }

    void ObserveEndpoints(bool observe) {
        Observe(_anchor?.Row);
        if (!ReferenceEquals(_anchor?.Row, _focus?.Row)) Observe(_focus?.Row);

        void Observe(ChatItemViewModel? row) {
            if (row is null) return;
            if (observe) row.PropertyChanged += OnEndpointChanged;
            else row.PropertyChanged -= OnEndpointChanged;
        }
    }

    void OnEndpointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateEndpointText();

    public void InvalidateEndpointText() {
        if (_endpointTextDirty) return;
        _endpointTextDirty = true;
        EndpointTextChanged?.Invoke();
    }

    public void CheckFeed() {
        if (_anchor is not null && !IsCurrent()) Clear();
    }

    bool IsCurrent() => ReferenceEquals(_feed, currentFeed()) && _basis == ProgressiveDisclosureBasis.Current;

    public int IndexOf(Point point) {
        if (point.IndexHint >= 0 && point.IndexHint < rows.Count && ReferenceEquals(rows[point.IndexHint], point.Row))
            return point.IndexHint;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], point.Row)) return i;
        return -1;
    }

    public bool TryRange(out (int Index, Point Point) start, out (int Index, Point Point) end, bool forPaint = false) {
        start = end = default;
        if (_anchor is not { } anchor || _focus is not { } focus) return false;
        var a = IndexOf(anchor);
        var b = ReferenceEquals(focus.Row, anchor.Row) ? a : IndexOf(focus);
        var checkText = !forPaint || _endpointTextDirty;
        // Tool call details can change without a notification from their group row.
        if (!IsCurrent() || a < 0 || b < 0 || ((checkText || anchor.Row is ToolGroupItem) && !Valid(anchor))
            || (a == b ? focus.Text != anchor.Text : (checkText || focus.Row is ToolGroupItem) && !Valid(focus))) {
            if (!forPaint) Clear();
            return false;
        }
        _endpointTextDirty = false;
        _anchor = anchor = anchor with { IndexHint = a };
        _focus = focus = focus with { IndexHint = b };
        (start, end) = a < b || (a == b && anchor.Offset <= focus.Offset)
            ? ((a, anchor), (b, focus)) : ((b, focus), (a, anchor));
        return true;
    }

    public bool CanStart(Point point) => currentFeed() is not null && IndexOf(point) >= 0 && Valid(point);

    public void Extend(Point focus) {
        CheckFeed();
        if (_anchor is { } anchor) Set(anchor, focus);
    }

    public string TextOf(ChatItemViewModel row) =>
        _focus is { } focus && ReferenceEquals(row, focus.Row) ? focus.Text
        : _anchor is { } anchor && ReferenceEquals(row, anchor.Row) ? anchor.Text : Read(row);

    public string Read(ChatItemViewModel row) => ChatSelectionText.For(row, Labels);

    bool Valid(Point point) {
        if (point.Row.SelectionKey != point.Key) return false;
        var shown = Read(point.Row);
        // A command's output can arrive after the selected command text.
        return shown == point.Text || point.Row is ShellCommandItem
            && shown.Length > point.Text.Length && shown[point.Text.Length] == '\n'
            && shown.StartsWith(point.Text, StringComparison.Ordinal);
    }

    public bool Move(int direction, bool message) {
        if (!TryRange(out _, out _) || _focus is not { } focus) return false;
        var index = IndexOf(focus);
        var text = focus.Text;
        if (!message && (direction < 0 ? focus.Offset > 0 : focus.Offset < text.Length)) {
            var starts = StringInfo.ParseCombiningCharacters(text);
            var at = focus.Offset == text.Length ? starts.Length : Array.BinarySearch(starts, focus.Offset);
            var target = at + direction;
            var next = focus with { Offset = target == starts.Length ? text.Length : starts[target] };
            Extend(next);
            return true;
        }
        for (index += direction; index >= 0 && index < rows.Count; index += direction) {
            var row = rows[index];
            if (row.SelectionKey is null || TextOf(row) is not { Length: > 0 } nextText) continue;
            var offset = message ? Math.Min(focus.Offset, nextText.Length) : direction < 0 ? nextText.Length : 0;
            var next = Point.At(row, nextText, offset, index);
            Extend(next);
            return true;
        }
        if (message) {
            var next = focus with { Offset = direction < 0 ? 0 : text.Length };
            Extend(next);
            return true;
        }
        return false;
    }

    public bool IsWithin(ChatItemViewModel row) =>
        HasRange && ReferenceEquals(_anchor?.Row, row) && ReferenceEquals(_focus?.Row, row);

    public string? CopyText() {
        if (!HasRange || !TryRange(out var start, out var end)) return null;
        var result = new StringBuilder();
        var included = false;
        for (var i = start.Index; i <= end.Index; i++) {
            var row = rows[i];
            var text = i == start.Index ? start.Point.Text
                : i == end.Index ? end.Point.Text : Read(row);
            var from = i == start.Index ? start.Point.Offset : 0;
            var to = i == end.Index ? end.Point.Offset : text.Length;
            if (text.Length == 0 && i != start.Index && i != end.Index) continue;
            if (included) result.Append('\n');
            result.Append(text.AsSpan(from, to - from));
            included = true;
        }
        // Remote access can change on another thread while a large selection is assembled.
        return ReferenceEquals(_feed, currentFeed()) && result.Length > 0 ? result.ToString() : null;
    }
}
