namespace Capacitor.Cli.Ide;

/// <param name="Phase"><c>loading</c>, <c>ready</c>, <c>no_work_item</c>, <c>signed_out</c>, <c>not_in_plan</c>,
/// <c>unreachable</c> or <c>session_unknown</c>. A ready or no-work-item section a later read could not refresh keeps its
/// phase and is <paramref name="Stale"/>.</param>
/// <param name="WorkItemUrl">The item's page on the server, when the server and the id are both known.</param>
/// <param name="State">The server's state kind as it sent it.</param>
/// <param name="Cycle"><c>none</c>, <c>cyclic</c> or <c>indeterminate</c>.</param>
/// <param name="Issues">The item's issues, less the one its title already names.</param>
internal sealed record WorkSection(
    string Phase,
    bool Stale,
    string? WorkItemId,
    string? WorkItemUrl,
    string? Key,
    string Title,
    string? Overview,
    string? State,
    string? PartOf,
    string[] BlockedBy,
    string Cycle,
    WorkPart[] Parts,
    WorkIssue[] Issues,
    WorkPerson[] People,
    int SessionCount) {
    // A record compares arrays by reference, and the host sends a section only when it differs from the one last sent.
    public bool Equals(WorkSection? other) =>
        other is not null
        && Phase == other.Phase && Stale == other.Stale && WorkItemId == other.WorkItemId && WorkItemUrl == other.WorkItemUrl
        && Key == other.Key && Title == other.Title && Overview == other.Overview && State == other.State && PartOf == other.PartOf
        && Cycle == other.Cycle && SessionCount == other.SessionCount
        && BlockedBy.SequenceEqual(other.BlockedBy) && Parts.SequenceEqual(other.Parts) && Issues.SequenceEqual(other.Issues)
        && People.SequenceEqual(other.People);

    public override int GetHashCode() => HashCode.Combine(Phase, Stale, WorkItemId, Title, SessionCount);
}
