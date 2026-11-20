using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// A work item's or part's status, in the order a work item page groups its parts.
public enum WorkStatus { InProgress, InReview, Blocked, Open, Done }

public static class WorkStatusExtensions {
    /// A status this app does not know yet reads as Open.
    public static WorkStatus ParseWorkStatus(string? wire) => wire switch {
        "in_progress" => WorkStatus.InProgress,
        "in_review"   => WorkStatus.InReview,
        "blocked"     => WorkStatus.Blocked,
        "done"        => WorkStatus.Done,
        _             => WorkStatus.Open,
    };

    public static string Wire(this WorkStatus status) => status switch {
        WorkStatus.InProgress => "in_progress",
        WorkStatus.InReview   => "in_review",
        WorkStatus.Blocked    => "blocked",
        WorkStatus.Done       => "done",
        _                     => "open",
    };

    public static string Label(this WorkStatus status) => status switch {
        WorkStatus.InProgress => "In progress",
        WorkStatus.InReview   => "In review",
        WorkStatus.Blocked    => "Blocked",
        WorkStatus.Done       => "Done",
        _                     => "Open",
    };

    public static string BrushKey(this WorkStatus status) => status switch {
        WorkStatus.InProgress => "KcapPurpleBrush",
        WorkStatus.InReview   => "KcapInfoBrush",
        WorkStatus.Blocked    => "KcapWarningBrush",
        WorkStatus.Done       => "KcapSuccessBrush",
        _                     => "KcapMutedBrush",
    };

    public static LucideIconKind Icon(this WorkStatus status) => status switch {
        WorkStatus.InProgress => LucideIconKind.CircleDot,
        WorkStatus.InReview   => LucideIconKind.Contrast,
        WorkStatus.Blocked    => LucideIconKind.CircleSlash,
        WorkStatus.Done       => LucideIconKind.CircleCheck,
        _                     => LucideIconKind.Circle,
    };
}
