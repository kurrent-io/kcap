using Markdig.Syntax.Inlines;

namespace Capacitor.App.ProgressiveDisclosure;

/// One faded run of text at a level below must-know. Runs that share a group are pieces of one
/// span, which can reach across blocks.
public sealed class GradedInline(ProgressiveDisclosureLevel level, object group) : ContainerInline {
    public ProgressiveDisclosureLevel Level { get; } = level;

    public object Group { get; } = group;
}
