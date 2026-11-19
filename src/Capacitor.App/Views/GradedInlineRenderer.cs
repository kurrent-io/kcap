using Avalonia.Controls.Documents;
using Capacitor.App.ProgressiveDisclosure;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

sealed class GradedInlineRenderer : AvaloniaObjectRenderer<GradedInline> {
    protected override void Write(AvaloniaRenderer renderer, GradedInline obj) {
        var span = new Span();
        span.Classes.Add(ProgressiveDisclosureHover.FadedClass);
        span.Classes.Add(obj.Level == ProgressiveDisclosureLevel.Footnote ? "progressive-disclosure-footnote" : "progressive-disclosure-detail");
        renderer.Push(span.Inlines);
        renderer.WriteChildren(obj);
        renderer.Pop();
        renderer.WriteInline(span);
    }
}
