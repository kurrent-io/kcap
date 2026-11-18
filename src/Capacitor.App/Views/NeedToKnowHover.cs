using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Lights the faded span under the pointer. Inlines take no pointer events, so the span is found by
/// hit-testing the block's text layout, which counts a line break as the platform's newline and an
/// embedded control as one character.
sealed class NeedToKnowHover {
    public const string FadedClass = "need-to-know-faded";
    public const string LitClass = "need-to-know-lit";

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
            // so the position is taken as found.
            span = FadedAt(inlines, block.TextLayout.HitTestPoint(point).TextPosition);
        }
        Light(span, span is null ? null : block);
    }

    public void Clear() => Light(null, null);

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
