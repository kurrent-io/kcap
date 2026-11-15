using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Client;

/// What the work-context pane shows of a session's work item. `PrimaryId` is the id the server
/// served, which for an absorbed item is the survivor's rather than the assignment's
/// `RequestedId`; a read that served no item keeps the id shown for the same assignment, else
/// `RequestedId` stands in. `StateKind` and `Cycle` are the server's words.
public sealed record WorkContextCard(
        WorkContextPhase Phase, bool IsStale, string? PrimaryId, string? RequestedId,
        string? Key, string Title, string? Overview, string? StateKind,
        string? PartOfTitle, IReadOnlyList<string> BlockedBy, string Cycle,
        IReadOnlyList<WorkContextPart> Parts, IReadOnlyList<WorkContextIssue> Issues, string? SeedIssueIdentity,
        IReadOnlyList<WorkContextPerson> Contributors, int SessionCount, ViewerIdentityDto? Viewer) {
    /// The rows Contributors was named from: a contributor the viewer owned needs its own name back
    /// once the viewer changes.
    internal IReadOnlyList<WorkItemContributorDto> ContributorRows { get; init; } = [];

    public static WorkContextCard Empty(WorkContextPhase phase) =>
        new(phase, false, null, null, null, "", null, null, null, [], "none", [], [], null, [], 0, null);

    public string DisplayTitle => Title.Length > 0 && !string.Equals(Title, Key, StringComparison.OrdinalIgnoreCase) ? Title
        : SeedIssue is { } seed && !HasFallbackTitle(seed) ? seed.Title : "";

    /// Every issue but the primary one when it folds into the title. An untitled issue folds only
    /// into a header that spells its key: `!N` does not name the issue `#N`.
    public IReadOnlyList<WorkContextIssue> SeparateIssues =>
        SeedIssue is { } seed && (HasFallbackTitle(seed)
            ? string.Equals(seed.Key, Key, StringComparison.OrdinalIgnoreCase)
            : string.Equals(seed.Title, DisplayTitle, StringComparison.OrdinalIgnoreCase))
            ? [.. Issues.Where(issue => !IsSeed(issue))]
            : Issues;

    /// A short key carries no repository, so only the identity names the primary issue.
    WorkContextIssue? SeedIssue => Issues.FirstOrDefault(IsSeed);

    bool IsSeed(WorkContextIssue issue) => string.Equals(issue.Identity, SeedIssueIdentity, StringComparison.Ordinal);

    static bool HasFallbackTitle(WorkContextIssue issue) =>
        issue.Title.Length == 0 || string.Equals(issue.Title, issue.Key, StringComparison.OrdinalIgnoreCase)
        || string.Equals(issue.Title, $"Issue {issue.Key}", StringComparison.OrdinalIgnoreCase);
}
