using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// One run of text at a level below must-know, shown as the reader's basis says.
public sealed class GradedInline(NeedToKnowLevel level, NeedToKnowDisplay display) : ContainerInline {
    public NeedToKnowLevel Level { get; } = level;

    public NeedToKnowDisplay Display { get; } = display;
}
