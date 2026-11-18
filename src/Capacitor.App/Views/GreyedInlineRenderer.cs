using Avalonia.Controls.Documents;
using Capacitor.App.NeedToKnow;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

sealed class GreyedInlineRenderer : AvaloniaObjectRenderer<GreyedInline> {
    protected override void Write(AvaloniaRenderer renderer, GreyedInline obj) {
        var span = new Span();
        span.Classes.Add("need-to-know-greyed");
        renderer.Push(span.Inlines);
        renderer.WriteChildren(obj);
        renderer.Pop();
        renderer.WriteInline(span);
    }
}
