using Capacitor.Cli.Core.WorkItems;
using Capacitor.Client;

namespace Capacitor.Cli.Ide;

internal static class ContextMapping {
    public static readonly PlanSection LoadingPlan     = Empty("loading");
    public static readonly PlanSection NoPlan          = Empty("none");
    public static readonly PlanSection UnavailablePlan = Empty("unavailable");
    public static readonly PlanSection SignedOutPlan   = Empty("signed_out");

    public static WorkSection Work(WorkContextCard card, string? serverUrl) => new(
        Phase: Phase(card.Phase),
        Stale: card.IsStale,
        WorkItemId: card.PrimaryId,
        WorkItemUrl: WorkItemUrl(card.PrimaryId, serverUrl),
        Key: card.Key,
        Title: card.DisplayTitle,
        Overview: card.Overview,
        State: card.StateKind,
        PartOf: card.PartOfTitle,
        BlockedBy: [.. card.BlockedBy],
        Cycle: card.Cycle,
        Parts: [.. card.Parts.Select(part => new WorkPart(part.Title, Mark(part.Mark)))],
        Issues: [.. card.SeparateIssues.Select(issue => new WorkIssue(issue.Key, issue.Title, issue.Url))],
        People: [.. card.Contributors.Select(Person)],
        SessionCount: card.SessionCount);

    public static PlanSection Plan(PlanBoard board) => new(
        "ready", [.. board.Tasks.Select(task => new PlanTask(task.TaskId, task.Title, task.Note, Status(task.State)))], board.DoneCount, board.Tasks.Count);

    static PlanSection Empty(string state) => new(state, [], 0, 0);

    static string? WorkItemUrl(string? id, string? serverUrl) =>
        WorkContextIds.ValidWorkItemId(id) is { } valid && !string.IsNullOrWhiteSpace(serverUrl)
            ? $"{serverUrl.TrimEnd('/')}/work-items/{Uri.EscapeDataString(valid)}"
            : null;

    /// <summary>The library words the viewer and a person it cannot name; the client words those itself.</summary>
    static WorkPerson Person(WorkContextPerson person) => new(
        person.IsViewer || person.Name == WorkContextCards.UnknownName ? null : person.Name, person.AvatarUrl, person.LastActivityAt, person.IsViewer);

    // A value added later goes out as its own name: a throw here would lose the read that carried it.
    static string Phase(WorkContextPhase phase) => phase switch {
        WorkContextPhase.WaitingForSession or WorkContextPhase.Loading => "loading",
        WorkContextPhase.Ready          => "ready",
        WorkContextPhase.NoWorkItem     => "no_work_item",
        WorkContextPhase.SignedOut      => "signed_out",
        WorkContextPhase.NotInPlan      => "not_in_plan",
        WorkContextPhase.Unreachable    => "unreachable",
        WorkContextPhase.SessionUnknown => "session_unknown",
        _                               => phase.ToString().ToLowerInvariant(),
    };

    static string Mark(WorkContextPartMark mark) => mark switch {
        WorkContextPartMark.Settled     => "settled",
        WorkContextPartMark.ThisSession => "this_session",
        WorkContextPartMark.Unknown     => "open",
        _                               => mark.ToString().ToLowerInvariant(),
    };

    static string Status(PlanTaskState state) => state switch {
        PlanTaskState.Pending    => "pending",
        PlanTaskState.InProgress => "in_progress",
        PlanTaskState.Completed  => "completed",
        PlanTaskState.Skipped    => "skipped",
        _                        => state.ToString().ToLowerInvariant(),
    };
}
