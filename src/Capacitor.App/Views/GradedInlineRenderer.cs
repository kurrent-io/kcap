using Avalonia.Controls.Documents;
using Capacitor.App.NeedToKnow;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

sealed class GradedInlineRenderer : AvaloniaObjectRenderer<GradedInline> {
    protected override void Write(AvaloniaRenderer renderer, GradedInline obj) {
        switch (obj.Display) {
            case NeedToKnowDisplay.Marked:
                var glyphs = obj.Level == NeedToKnowLevel.Footnote ? 2 : 1;
                renderer.WriteInline(Glyph(new string(NeedToKnowMarks.Open, glyphs)));
                renderer.WriteChildren(obj);
                renderer.WriteInline(Glyph(new string(NeedToKnowMarks.Close, glyphs)));
                return;
            case NeedToKnowDisplay.Faded:
                var span = new Span();
                span.Classes.Add(NeedToKnowHover.FadedClass);
                span.Classes.Add(obj.Level == NeedToKnowLevel.Footnote ? "need-to-know-footnote" : "need-to-know-detail");
                renderer.Push(span.Inlines);
                renderer.WriteChildren(obj);
                renderer.Pop();
                renderer.WriteInline(span);
                return;
            default:
                renderer.WriteChildren(obj);
                return;
        }
    }

    static Run Glyph(string text) {
        var run = new Run(text);
        run.Classes.Add("need-to-know-glyph");
        return run;
    }
}
