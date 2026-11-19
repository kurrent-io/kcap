using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// One faded run of text at a level below must-know.
public sealed class GradedInline(NeedToKnowLevel level) : ContainerInline {
    public NeedToKnowLevel Level { get; } = level;
}
