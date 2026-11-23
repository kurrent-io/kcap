namespace Capacitor.App.ViewModels;

/// An open work item of the picked project: its title, the parts done out of all and its status, the same for every
/// viewer, the part title of each session in it the viewer can see, keyed by session id, the owners of those sessions
/// and when the latest of them last moved. Value-equal on the sessions it holds, so a read that changes nothing
/// replaces nothing.
public sealed record RailWorkItem(
        string Id, string Title, int PartsDone, int PartsTotal, IReadOnlyDictionary<string, string> PartBySession,
        WorkStatus Status = WorkStatus.Open, IReadOnlyList<WorkItemPerson>? People = null, DateTimeOffset? LastActivityAt = null) {
    public const string NoPart = "No part";

    public bool Equals(RailWorkItem? other) =>
        other is not null && Id == other.Id && Title == other.Title && PartsDone == other.PartsDone && PartsTotal == other.PartsTotal
        && Status == other.Status && LastActivityAt == other.LastActivityAt && PartBySession.Count == other.PartBySession.Count
        && PartBySession.All(entry => other.PartBySession.TryGetValue(entry.Key, out var part) && part == entry.Value);

    public override int GetHashCode() => HashCode.Combine(Id, Title, PartsDone, PartsTotal, Status, LastActivityAt, PartBySession.Count);
}
