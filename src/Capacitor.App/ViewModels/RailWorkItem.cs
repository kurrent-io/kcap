using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.ViewModels;

/// An open work item of the picked project: its title, the parts done out of all and its status, the same for every
/// viewer, the part title of each session in it the viewer can see, keyed by session id, and those sessions newest
/// first. Value-equal on the sessions it holds, so a read that changes nothing replaces nothing.
public sealed record RailWorkItem(
        string Id, string Title, int PartsDone, int PartsTotal, IReadOnlyDictionary<string, string> PartBySession,
        IReadOnlyList<WorkItemSessionRefDto> Sessions, WorkStatus Status = WorkStatus.Open) {
    public const string NoPart = "No part";
    const int LatestShown = 5;

    /// Null from a server older than the sessions' last activity.
    public DateTimeOffset? LastActivityAt => Sessions.Max(session => session.LastActivityAt);

    public IReadOnlyList<WorkItemPerson> People => [.. Sessions.Select(session => session.Owner).OfType<RepoSessionOwnerDto>()
        .DistinctBy(owner => owner.UserId).Select(owner => new WorkItemPerson(owner.UserId, owner.DisplayName ?? owner.Username, owner.AvatarUrl))];

    /// The sessions the rail lists under it whether or not the session list has paged them in, with their owners.
    public IReadOnlyDictionary<string, PinPresentation> Latest => Sessions.Take(LatestShown).ToDictionary(
        session => session.SessionId,
        session => new PinPresentation(session.Owner?.Username, session.Owner?.DisplayName, session.Owner?.AvatarUrl), StringComparer.Ordinal);

    /// The work item as one of the viewer's: only the sessions they own.
    public RailWorkItem OwnedBy(IReadOnlySet<string> viewerIds) =>
        this with { Sessions = [.. Sessions.Where(session => session.Owner?.UserId is { } owner && viewerIds.Contains(owner))] };

    public bool Equals(RailWorkItem? other) =>
        other is not null && Id == other.Id && Title == other.Title && PartsDone == other.PartsDone && PartsTotal == other.PartsTotal
        && Status == other.Status && PartBySession.Count == other.PartBySession.Count
        && PartBySession.All(entry => other.PartBySession.TryGetValue(entry.Key, out var part) && part == entry.Value)
        && Sessions.SequenceEqual(other.Sessions);

    public override int GetHashCode() => HashCode.Combine(Id, Title, PartsDone, PartsTotal, Status, Sessions.Count);
}
