using Lucide.Avalonia;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The Lucide icon for each glyph the band draws.</summary>
public static class NextWorkIcons {
    public static LucideIconKind For(string icon) => icon switch {
        "pr"        => LucideIconKind.GitPullRequest,
        "merge"     => LucideIconKind.GitMerge,
        "bug"       => LucideIconKind.Bug,
        "session"   => LucideIconKind.MessageSquare,
        "loose_end" => LucideIconKind.Flag,
        "evals"     => LucideIconKind.ClipboardCheck,
        "check"     => LucideIconKind.Check,
        "person"    => LucideIconKind.User,
        "link"      => LucideIconKind.Link,
        "tag"       => LucideIconKind.Tag,
        "clock"     => LucideIconKind.Clock,
        "eye"       => LucideIconKind.Eye,
        "layers"    => LucideIconKind.Layers,
        "refresh"   => LucideIconKind.RefreshCw,
        "dot"       => LucideIconKind.Dot,
        _           => LucideIconKind.CircleDot,
    };
}
