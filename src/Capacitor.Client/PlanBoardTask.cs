using Capacitor.Cli.Core.Plans;

namespace Capacitor.Client;

/// One task of a plan board. `TaskId` is the server's id, or `#` and its ordinal for a task sent without one.
public sealed record PlanBoardTask(string TaskId, string Title, string? Note, PlanTaskState State) {
    public static PlanBoardTask From(PlanLedgerTaskDto task) => new(
        task.TaskId is { Length: > 0 } id ? id : $"#{task.Ordinal}",
        task.Title,
        string.IsNullOrWhiteSpace(task.Note) ? null : task.Note,
        StateOf(task.Status));

    static PlanTaskState StateOf(string status) => status switch {
        "in_progress" => PlanTaskState.InProgress,
        "completed"   => PlanTaskState.Completed,
        "skipped"     => PlanTaskState.Skipped,
        _             => PlanTaskState.Pending,
    };
}
