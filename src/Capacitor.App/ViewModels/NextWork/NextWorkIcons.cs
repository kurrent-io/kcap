using Lucide.Avalonia;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The Lucide icon for each kind of row the list draws.</summary>
public static class NextWorkIcons {
    public static LucideIconKind For(string icon) => icon switch {
        "pr"        => LucideIconKind.GitPullRequest,
        "bug"       => LucideIconKind.Bug,
        "session"   => LucideIconKind.MessageSquare,
        "loose_end" => LucideIconKind.Flag,
        "evals"     => LucideIconKind.ClipboardCheck,
        _           => LucideIconKind.CircleDot,
    };

}
