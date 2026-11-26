namespace Capacitor.App.ViewModels;

/// How many of the plan's tasks are in one state, as the collapsed sidebar section shows it.
public sealed record PlanTaskCount(PlanTaskState State, int Count, bool IsActive = false) {
    /// In progress in a session that has ended.
    public bool IsStarted => State == PlanTaskState.InProgress && !IsActive;

    /// The count in words, for the tooltip and screen readers: the mark beside the number carries
    /// no text. Worded as the task rows' state labels.
    public string Label => State switch {
        PlanTaskState.InProgress => IsActive ? $"{Count} in progress" : $"{Count} started",
        PlanTaskState.Completed  => $"{Count} done",
        PlanTaskState.Skipped    => $"{Count} skipped",
        _                        => $"{Count} pending",
    };
}
