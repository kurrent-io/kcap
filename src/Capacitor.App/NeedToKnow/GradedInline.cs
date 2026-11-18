using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// One run of text at a level below must-know, shown as the reader's basis says. Deep greys it a step
/// further, to keep it apart from greyed worth-knowing text.
public sealed class GradedInline(NeedToKnowLevel level, NeedToKnowDisplay display, bool deep = false) : ContainerInline {
    public NeedToKnowLevel Level { get; } = level;

    public NeedToKnowDisplay Display { get; } = display;

    public bool Deep { get; } = deep;
}
