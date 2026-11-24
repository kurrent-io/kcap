using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed class ChatSelectionOverlay : Avalonia.Controls.Primitives.TemplatedControl {
    internal Action<DrawingContext>? Paint { get; set; }

    public override void Render(DrawingContext context) => Paint?.Invoke(context);

    internal readonly record struct Run(TextBlock Block, string Text, string ProjectionText, int Start, int Length, int Leading) {
        public int ToProjection(int nativeOffset) => nativeOffset == 0 ? Start - Leading
            : Start + Unnormalize(Text.AsSpan(), nativeOffset, ProjectionText.AsSpan(Start, Length));

        public int ToNative(int offset) => Unnormalize(ProjectionText.AsSpan(Start, Length), offset - Start, Text.AsSpan());

        static int Unnormalize(ReadOnlySpan<char> from, int offset, ReadOnlySpan<char> to) {
            if (from.SequenceEqual(to)) return Math.Clamp(offset, 0, to.Length);
            var i = 0;
            var j = 0;
            while (i < from.Length && j < to.Length && i < offset) {
                i += from[i] == '\r' && i + 1 < from.Length && from[i + 1] == '\n' ? 2 : 1;
                j += to[j] == '\r' && j + 1 < to.Length && to[j + 1] == '\n' ? 2 : 1;
            }
            return j;
        }
    }

    internal static IEnumerable<Run> Runs(Control row, string text) {
        var cursor = 0;
        var normalized = Normalize(text);
        foreach (var (block, rendered, marker) in Blocks(row)) {
            if (rendered.Length == 0 || !block.IsEffectivelyVisible) continue;
            var whole = Normalize(rendered);
            var native = whole;
            var index = normalized.IndexOf(native, cursor, StringComparison.Ordinal);
            if (index < 0 && marker > 0) {
                native = Normalize(rendered[marker..]);
                index = normalized.IndexOf(native, cursor, StringComparison.Ordinal);
            }
            if (index < 0) continue;
            var markerIncluded = native.Length == whole.Length ? marker : 0;
            var start = OriginalOffset(text, index + markerIncluded);
            cursor = index + native.Length;
            var end = OriginalOffset(text, cursor);
            yield return new(block, rendered[marker..], text, start, end - start, markerIncluded);
        }
    }

    static IEnumerable<(TextBlock Block, string Text, int Marker)> Blocks(Control row) {
        foreach (var child in row.GetSelfAndVisualDescendants()) {
            if (child is MarkdownView { IsEffectivelyVisible: true } markdown) {
                foreach (var block in markdown.SelectionBlocks()) yield return block;
            } else if (child is TextBlock { IsEffectivelyVisible: true, Text: { Length: > 0 } text } block
                && !block.Classes.Contains("messageAuthor")
                && (block is SelectableTextBlock || block.Classes.Contains("toolLine")
                    || block.Classes.Contains("toolInspectionHeading") || block.Classes.Contains("toolInspectionNote")
                    || block.Classes.Contains("toolKindChip"))
                && block.FindAncestorOfType<MarkdownView>() is null) {
                yield return (block, text, 0);
            }
        }
    }

    static string Normalize(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    static int OriginalOffset(string text, int offset) {
        if (!text.Contains('\r')) return offset;
        var at = 0;
        for (var i = 0; i < offset && at < text.Length; i++, at++)
            if (text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n') at++;
        return at;
    }

    internal static Rect? BoundsIn(Visual visual, Visual target) =>
        visual.TranslatePoint(default, target) is { } origin ? new Rect(origin, visual.Bounds.Size) : null;

    internal static Rect? Caret(Control row, ChatSelection.Point point, Visual relativeTo) {
        Run? nearest = null;
        var distance = int.MaxValue;
        foreach (var run in Runs(row, point.Text)) {
            var within = Math.Clamp(point.Offset, run.Start, run.Start + run.Length);
            var gap = Math.Abs(within - point.Offset);
            if (gap >= distance) continue;
            nearest = run;
            distance = gap;
        }
        if (nearest is not { } target
            || target.Block.TranslatePoint(new Point(target.Block.Padding.Left, target.Block.Padding.Top), relativeTo) is not { } origin)
            return null;
        var native = target.ToNative(Math.Clamp(point.Offset, target.Start, target.Start + target.Length));
        return target.Block.TextLayout.HitTestTextPosition(native).Translate(origin);
    }

    internal static ChatSelection.Point? Hit(Control container, ChatItemViewModel row, string text, Visual relativeTo, Point point,
            int index, out double verticalGap, Rect? viewport = null) {
        verticalGap = double.PositiveInfinity;
        var closest = double.PositiveInfinity;
        var offset = -1;
        foreach (var run in Runs(container, text)) {
            if (BoundsIn(run.Block, relativeTo) is not { } bounds || viewport is { } clip && !bounds.Intersects(clip)) continue;
            var x = Math.Clamp(point.X, bounds.Left, bounds.Right);
            var y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
            var gap = Math.Abs(point.Y - y);
            var distance = new Vector(point.X - x, point.Y - y).SquaredLength;
            if (viewport is not null) {
                if (gap > verticalGap) continue;
                if (gap < verticalGap) closest = double.PositiveInfinity;
            }
            if (distance >= closest) continue;
            var local = new Point(x - bounds.X - run.Block.Padding.Left, y - bounds.Y - run.Block.Padding.Top);
            offset = viewport is not null && point.Y < bounds.Top ? run.ToProjection(0)
                : viewport is not null && point.Y >= bounds.Bottom ? run.Start + run.Length
                : run.ToProjection(run.Block.TextLayout.HitTestPoint(local).TextPosition);
            verticalGap = gap;
            closest = distance;
        }
        return offset >= 0 ? ChatSelection.Point.At(row, text, offset, index) : null;
    }
}
