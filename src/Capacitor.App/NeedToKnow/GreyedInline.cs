using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// Text the reader asked to see greyed rather than hidden.
public sealed class GreyedInline(NeedToKnowLevel level) : ContainerInline {
    public NeedToKnowLevel Level { get; } = level;
}
