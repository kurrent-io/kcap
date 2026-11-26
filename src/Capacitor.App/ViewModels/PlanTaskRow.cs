using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One task of the plan. Mutable so a re-read changes a row in place: replacing it would rebuild
/// its container and restart the in-progress pulse on every poll.
public sealed class PlanTaskRow(string taskId) : ReactiveObject {
    public string TaskId { get; } = taskId;

    public string Title { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string? Note { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    public PlanTaskState State {
        get;
        private set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsInProgress));
            this.RaisePropertyChanged(nameof(IsCompleted));
            this.RaisePropertyChanged(nameof(IsSettled));
            this.RaisePropertyChanged(nameof(IsStarted));
            this.RaisePropertyChanged(nameof(StateLabel));
        }
    }

    public bool IsInProgress => State == PlanTaskState.InProgress;
    public bool IsCompleted  => State == PlanTaskState.Completed;
    /// The server's own rule for progress: a skipped task is as settled as a completed one.
    public bool IsSettled    => State is PlanTaskState.Completed or PlanTaskState.Skipped;

    /// In progress in a session that is still running: the only state that pulses.
    public bool IsActive {
        get;
        private set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsStarted));
            this.RaisePropertyChanged(nameof(StateLabel));
        }
    }

    /// In progress in a session that has ended: the task was started and never settled.
    public bool IsStarted => IsInProgress && !IsActive;

    /// The mark's state in words, so it is not told by colour or shape alone.
    public string StateLabel => State switch {
        PlanTaskState.InProgress => IsActive ? "In progress" : "Started",
        PlanTaskState.Completed  => "Done",
        PlanTaskState.Skipped    => "Skipped",
        _                        => "Pending",
    };

    public void Present(PlanBoardTask task, bool sessionOver) {
        Title = task.Title;
        Note = task.Note;
        State = task.State;
        Present(sessionOver);
    }

    public void Present(bool sessionOver) => IsActive = IsInProgress && !sessionOver;
}
