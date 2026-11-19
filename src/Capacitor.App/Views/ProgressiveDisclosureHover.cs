using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Lights the faded span under the pointer. Inlines take no pointer events, so the span is found by
/// hit-testing the block's text layout, which counts a line break as the platform's newline and an
/// embedded control as one character.
sealed class ProgressiveDisclosureHover {
    public const string FadedClass = "progressive-disclosure-faded";
    public const string LitClass = "progressive-disclosure-lit";

    Span? _lit;
    TextBlock? _litBlock;

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
        Light(span, span is null ? null : block);
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

    void Light(Span? span, TextBlock? block) {
        if (ReferenceEquals(span, _lit)) return;
        _lit?.Classes.Remove(LitClass);
        _litBlock?.InvalidateVisual();
        _lit = span;
        _litBlock = block;
        _lit?.Classes.Add(LitClass);
        _litBlock?.InvalidateVisual();
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
