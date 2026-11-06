namespace Capacitor.App.ViewModels;

/// What a session's transcript shows still running.
public readonly record struct RunningWork(int Commands, int Subagents) {
    public bool IsIdle => Commands == 0 && Subagents == 0;
}
