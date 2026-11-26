using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Lights the faded span under the pointer, with every other piece of its span when it reaches across
/// blocks. Inlines take no pointer events, so the span is found by hit-testing the block's text layout,
/// which counts a line break as the platform's newline and an embedded control as one character.
sealed class ProgressiveDisclosureHover {
    public const string FadedClass = "progressive-disclosure-faded";
    public const string LitClass = "progressive-disclosure-lit";

    static readonly ConditionalWeakTable<Span, object> Groups = new();

    Span? _hit;
    readonly List<(Span Span, TextBlock Block)> _lit = [];

    /// Marks the rendered span as a piece of the graded span the group stands for.
    public static void SetGroup(Span span, object group) => Groups.AddOrUpdate(span, group);

    /// The block comes from the view's own visuals at the point rather than the event's source, which
    /// can be an overlay above the text.
    public void Move(Visual view, PointerEventArgs e) {
        Span? span = null;
        var block = view.GetVisualsAt(e.GetPosition(view)).OfType<TextBlock>().FirstOrDefault(b => b.Inlines is { Count: > 0 });
        if (block is { Inlines: { } inlines }) {
            var point = e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top);
            // IsInside reads false on every wrapped line once the line height exceeds the glyphs',
            // so only the horizontal extent of the line under the point is checked.
            if (OnText(block.TextLayout, point))
                span = FadedAt(inlines, block.TextLayout.HitTestPoint(point).CharacterHit.FirstCharacterIndex);
        }
        Light(view, span);
    }

    public void Clear() => Light(null, null);

    /// The hit test snaps to the nearest character, so a point past the end of a line would light it.
    static bool OnText(Avalonia.Media.TextFormatting.TextLayout layout, Point point) {
        var top = 0.0;
        foreach (var line in layout.TextLines) {
            if (point.Y < top + line.Height)
                return point.Y >= top && point.X >= line.Start && point.X <= line.Start + line.WidthIncludingTrailingWhitespace;
            top += line.Height;
        }
        return false;
    }

    void Light(Visual? view, Span? span) {
        if (ReferenceEquals(span, _hit)) return;
        foreach (var (lit, block) in _lit) {
            lit.Classes.Remove(LitClass);
            block.InvalidateVisual();
        }
        _lit.Clear();
        _hit = span;
        if (view is null || span is null) return;
        foreach (var piece in Pieces(view, span)) {
            piece.Span.Classes.Add(LitClass);
            piece.Block.InvalidateVisual();
            _lit.Add(piece);
        }
    }

    /// The span's pieces in every block of the view, the hit one among them.
    static IEnumerable<(Span Span, TextBlock Block)> Pieces(Visual view, Span span) {
        if (!Groups.TryGetValue(span, out var group)) group = span;
        foreach (var block in view.GetVisualDescendants().OfType<TextBlock>()) {
            if (block.Inlines is not { Count: > 0 } inlines) continue;
            foreach (var faded in FadedIn(inlines))
                if (ReferenceEquals(faded, span) || Groups.TryGetValue(faded, out var other) && ReferenceEquals(other, group))
                    yield return (faded, block);
        }
    }

    static IEnumerable<Span> FadedIn(InlineCollection inlines) {
        foreach (var span in inlines.OfType<Span>()) {
            if (span.Classes.Contains(FadedClass)) yield return span;
            foreach (var inner in FadedIn(span.Inlines)) yield return inner;
        }
    }

    static Span? FadedAt(InlineCollection inlines, int index) {
        var offset = 0;
        return Find(inlines, index, ref offset, null);
    }

    static Span? Find(InlineCollection inlines, int index, ref int offset, Span? faded) {
        foreach (var inline in inlines) {
            var start = offset;
            switch (inline) {
                case Span span:
                    var inner = span.Classes.Contains(FadedClass) ? span : faded;
                    if (Find(span.Inlines, index, ref offset, inner) is { } found) return found;
                    continue;
                case Run run:
                    offset += run.Text?.Length ?? 0;
                    break;
                case LineBreak:
                    offset += Environment.NewLine.Length;
                    break;
                default:
                    offset += 1;
                    break;
            }
            if (faded is not null && index >= start && index < offset) return faded;
            if (index < offset) return null;
        }
        return null;
    }
}
