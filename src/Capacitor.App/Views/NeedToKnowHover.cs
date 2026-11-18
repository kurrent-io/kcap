using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Lights the greyed span under the pointer. Inlines take no pointer events, so the span is found by
/// hit-testing the block's text layout, which counts a line break as the platform's newline and an
/// embedded control as one character.
sealed class NeedToKnowHover {
    public const string GreyedClass = "need-to-know-greyed";
    public const string LitClass = "need-to-know-lit";

    Span? _lit;

    public void Move(PointerEventArgs e) {
        Span? span = null;
        if (BlockUnder(e.Source as Visual) is { Inlines: { } inlines } block) {
            var point = e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top);
            var hit = block.TextLayout.HitTestPoint(point);
            if (hit.IsInside) span = GreyedAt(inlines, hit.TextPosition);
        }
        Light(span);
    }

    public void Clear() => Light(null);

    void Light(Span? span) {
        if (ReferenceEquals(span, _lit)) return;
        _lit?.Classes.Remove(LitClass);
        _lit = span;
        _lit?.Classes.Add(LitClass);
    }

    static TextBlock? BlockUnder(Visual? visual) {
        for (; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBlock block) return block;
        return null;
    }

    static Span? GreyedAt(InlineCollection inlines, int index) {
        var offset = 0;
        return Find(inlines, index, ref offset, null);
    }

    static Span? Find(InlineCollection inlines, int index, ref int offset, Span? greyed) {
        foreach (var inline in inlines) {
            var start = offset;
            switch (inline) {
                case Span span:
                    var inner = span.Classes.Contains(GreyedClass) ? span : greyed;
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
            if (greyed is not null && index >= start && index < offset) return greyed;
            if (index < offset) return null;
        }
        return null;
    }
}
