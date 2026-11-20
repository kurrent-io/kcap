namespace Capacitor.App.ViewModels;

/// A work item holding a listed session of the picked project: its title, the parts done out of all, whether it is done
/// and its status, the same for every viewer, the part title of each listed session in it, keyed by session id, and the
/// owners of those sessions. Value-equal on the sessions it holds, so a read that changes nothing
/// replaces nothing.
public sealed record RailWorkItem(
        string Id, string Title, int PartsDone, int PartsTotal, IReadOnlyDictionary<string, string> PartBySession, bool Done = false,
        WorkStatus Status = WorkStatus.Open, IReadOnlyList<WorkItemPerson>? People = null) {
    public const string NoPart = "No part";

    public bool Equals(RailWorkItem? other) =>
        other is not null && Id == other.Id && Title == other.Title && PartsDone == other.PartsDone && PartsTotal == other.PartsTotal
        && Done == other.Done && Status == other.Status && PartBySession.Count == other.PartBySession.Count
        && PartBySession.All(entry => other.PartBySession.TryGetValue(entry.Key, out var part) && part == entry.Value);

    public override int GetHashCode() => HashCode.Combine(Id, Title, PartsDone, PartsTotal, Done, Status, PartBySession.Count);
}
