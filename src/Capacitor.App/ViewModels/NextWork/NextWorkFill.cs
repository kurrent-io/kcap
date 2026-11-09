namespace Capacitor.App.ViewModels.NextWork;

/// <summary>A next-work task picked for the next launch: <see cref="Goal"/>, the line the launch opens with (the item's
/// title, or what to do with a pull request); the machine and checkout to run it in when one is known; and
/// <see cref="Context"/>, the prompt the server built for the item, sent last. <see cref="Label"/> names it on the
/// composer's chip, and <see cref="ContextSummary"/> says there what the context holds, since the prompt is written for
/// the agent. <see cref="AwaitsCheckout"/>: the task has a repository but no checkout of it is known, so the viewer
/// picks one before it can start.</summary>
public sealed record NextWorkFill(string Key, string Label, string Goal, string? Machine, bool MachineIsLocal, string? RepoPath,
    string? Context = null, string? ContextSummary = null, bool AwaitsCheckout = false) {
    /// <summary>A work item or loose end, whose launch the server claims and attaches.</summary>
    public bool IsLaunchTarget =>
        Key.StartsWith(NextWorkLaneLogic.WorkItemPrefix, StringComparison.Ordinal) || Key.StartsWith(NextWorkLaneLogic.LooseEndPrefix, StringComparison.Ordinal);
}
