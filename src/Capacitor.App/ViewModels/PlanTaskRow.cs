using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One task of the plan. Mutable so a re-read changes a row in place: replacing it would rebuild
/// its container and restart the in-progress pulse on every poll.
public sealed class PlanTaskRow(string taskId) : ReactiveObject {
    public string TaskId { get; } = taskId;

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _note;
    public string? Note { get => _note; private set => this.RaiseAndSetIfChanged(ref _note, value); }

    PlanTaskState _state;
    public PlanTaskState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsInProgress));
            this.RaisePropertyChanged(nameof(IsCompleted));
            this.RaisePropertyChanged(nameof(IsSettled));
        }
    }

    public bool IsInProgress => State == PlanTaskState.InProgress;
    public bool IsCompleted  => State == PlanTaskState.Completed;
    /// The server's own rule for progress: a skipped task is as settled as a completed one.
    public bool IsSettled    => State is PlanTaskState.Completed or PlanTaskState.Skipped;

    bool _isActive;
    /// In progress in a session that is still running: the only state that pulses.
    public bool IsActive { get => _isActive; private set => this.RaiseAndSetIfChanged(ref _isActive, value); }

    public void Present(PlanBoardTask task, bool sessionOver) {
        Title = task.Title;
        Note = task.Note;
        State = task.State;
        Present(sessionOver);
    }

    public void Present(bool sessionOver) => IsActive = IsInProgress && !sessionOver;
}
