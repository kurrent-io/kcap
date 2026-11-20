using Markdig.Syntax.Inlines;

namespace Capacitor.App.ProgressiveDisclosure;

/// One faded run of text at a level below must-know.
public sealed class GradedInline(ProgressiveDisclosureLevel level) : ContainerInline {
    public ProgressiveDisclosureLevel Level { get; } = level;
}
