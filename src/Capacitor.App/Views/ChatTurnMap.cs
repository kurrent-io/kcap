using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

internal sealed class ChatTurnMap : Control {
    internal const int MaxBuckets = 96;
    internal const double HeaderHeight = 32;

    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<ChatTurnMap, IBrush?>(nameof(MarkBrush));
    public static readonly StyledProperty<IBrush?> ViewportBrushProperty =
        AvaloniaProperty.Register<ChatTurnMap, IBrush?>(nameof(ViewportBrush));

    public IBrush? MarkBrush {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    public IBrush? ViewportBrush {
        get => GetValue(ViewportBrushProperty);
        set => SetValue(ViewportBrushProperty, value);
    }

    internal ChatTurnIndex? Index { get; set; }
    internal ScrollViewer? Scroll { get; set; }
    internal ChatVirtualizingPanel? Panel { get; set; }
    internal bool IsWide { get; set; }
    internal int PaintedBuckets { get; private set; }

    static ChatTurnMap() => AffectsRender<ChatTurnMap>(MarkBrushProperty, ViewportBrushProperty);

    bool GetScale(out double extent, out double height, out double inset) {
        extent = Scroll?.Extent.Height ?? 0;
        height = Bounds.Height - HeaderHeight - 4;
        inset = 0;
        if (Scroll is not { } scroll || Panel?.TranslatePoint(default, scroll) is not { } origin
            || extent <= 0 || height <= 0) return false;
        inset = origin.Y + scroll.Offset.Y;
        return true;
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        PaintedBuckets = 0;
        if (MarkBrush is not { } ink || ViewportBrush is not { } viewportBrush
            || Index?.Starts is not { Count: > 0 } starts || Scroll is not { } scroll
            || Panel is not { } panel || !GetScale(out var extent, out var height, out var inset)) return;

        var buckets = Math.Clamp((int)(height / 4), 1, MaxBuckets);
        var width = IsWide ? Math.Min(16, Bounds.Width - 4) : 6;
        if (width <= 0) return;
        var x = (Bounds.Width - width) / 2;
        var step = height / buckets;
        var next = 0;
        var outlineInk = new Pen(ink);
        for (var bucket = 0; bucket < buckets && next < starts.Count; bucket++) {
            var start = starts[next];
            var end = extent * (bucket + 1) / buckets - inset;
            if (panel.ProjectedRowStart(start.ProjectedIndex) >= end) continue;
            var lastRow = panel.ProjectedRowAt(end);
            var bucketEnd = panel.ProjectedRowStart(lastRow) < end ? lastRow + 1 : lastRow;
            var after = Math.Max(next + 1, Index.LowerBound(bucketEnd));
            var multiple = after - next > 1;
            next = after;
            var length = multiple || start.Number > 0 ? width : width / 2;
            var y = HeaderHeight + bucket * step + step / 2;
            if (start.Folded)
                context.DrawRectangle(null, outlineInk, new Rect(x, y - 1, length, 2));
            else
                context.DrawRectangle(ink, null, new Rect(x, y - 0.75, length, multiple ? 2.5 : 1.5));
            PaintedBuckets++;
        }

        var viewportHeight = Math.Clamp(scroll.Viewport.Height / extent * height, Math.Min(7, height), height);
        var viewportTop = HeaderHeight + Math.Clamp(scroll.Offset.Y / extent * height, 0, height - viewportHeight);
        context.DrawRectangle(null, new Pen(viewportBrush, 2),
            new Rect(2, viewportTop, Math.Max(0, Bounds.Width - 4), viewportHeight), 2, 2);
    }

    internal int DestinationAt(Point point) {
        if (point.Y < HeaderHeight || Index?.Starts is not { Count: > 0 } starts
            || Panel is not { } panel || !GetScale(out var extent, out var height, out var inset)) return -1;
        var y = Math.Clamp((point.Y - HeaderHeight) / height, 0, 1) * extent - inset;
        var row = panel.ProjectedRowAt(y);
        var index = Index.LowerBound(row);
        if (index == starts.Count || starts[index].ProjectedIndex > row) index--;
        return Math.Max(0, index);
    }
}
