using Capacitor.Cli.Core.Plans;

namespace Capacitor.Client;

/// The plan a session works from, as an ordered list of tasks.
public sealed record PlanBoard(IReadOnlyList<PlanBoardTask> Tasks) {
    /// The server's own rule for progress: a skipped task is as settled as a completed one.
    public int DoneCount => Tasks.Count(task => task.State is PlanTaskState.Completed or PlanTaskState.Skipped);

    /// The current plan's board, else the first plan's; null for a read that is not Ready or names no plan.
    public static PlanBoard? From(SessionPlansRead read) {
        if (read.Kind != SessionPlansReadKind.Ready) return null;
        var plan = read.Plans.FirstOrDefault(p => p.IsCurrent) ?? (read.Plans.Count > 0 ? read.Plans[0] : null);
        return plan is null ? null : new PlanBoard([.. plan.Tasks.OrderBy(task => task.Ordinal).Select(PlanBoardTask.From)]);
    }
}
