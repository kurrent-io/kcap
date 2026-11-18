using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Capacitor.App.NeedToKnow;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A collapsed run leaves a small mark whose tooltip holds its text, so the reader can tell something is there.
sealed class GradedInlineRenderer : AvaloniaObjectRenderer<GradedInline> {
    protected override void Write(AvaloniaRenderer renderer, GradedInline obj) {
        var level = obj.Level == NeedToKnowLevel.OnlyIfCurious ? "need-to-know-curious" : "need-to-know-worth";
        switch (obj.Display) {
            case NeedToKnowDisplay.Collapsed:
                var mark = new TextBlock { Text = "⋯", VerticalAlignment = VerticalAlignment.Center };
                mark.Classes.Add("need-to-know-collapsed");
                ToolTip.SetTip(mark, new TextBlock { Text = KcapMarkdownExtension.Label(obj), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 });
                renderer.WriteInline(new InlineUIContainer(mark));
                return;
            case NeedToKnowDisplay.Marked:
                var glyphs = obj.Level == NeedToKnowLevel.OnlyIfCurious ? 2 : 1;
                renderer.WriteInline(Glyph(new string(NeedToKnowMarks.Open, glyphs)));
                renderer.WriteChildren(obj);
                renderer.WriteInline(Glyph(new string(NeedToKnowMarks.Close, glyphs)));
                return;
            case NeedToKnowDisplay.Greyed:
                var span = new Span();
                span.Classes.Add("need-to-know-greyed");
                span.Classes.Add(level);
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
