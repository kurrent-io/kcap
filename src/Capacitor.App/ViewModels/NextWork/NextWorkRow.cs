using Capacitor.Remote.Models;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>One engine candidate as the band reads it: the wire row with its optional fields settled.</summary>
public sealed record NextWorkRow(
    string                          TargetKey,
    string                          TargetKind,
    string?                         TargetId,
    string                          TargetLabel,
    string?                         TargetHref,
    string?                         RepoHash,
    string                          Arm,
    int                             Tier,
    int?                            Rank,
    string                          Because,
    NextWorkFacets                  Facets,
    IReadOnlyList<NextWorkEvidence> Evidence,
    bool                            TrackerDependent = false,
    DateTimeOffset?                 TrackerStateAsOf = null) {
    /// <summary>Null for a row with no target key.</summary>
    public static NextWorkRow? From(NextWorkCandidateDto c) {
        if (string.IsNullOrWhiteSpace(c.TargetKey)) return null;

        var f = c.Facets;
        return new(
            c.TargetKey, c.TargetKind ?? "", c.TargetId, c.TargetLabel ?? "", c.TargetHref, c.RepoHash, c.Arm ?? "", c.Tier,
            c.Rank, c.Because ?? "",
            f is null ? NextWorkFacets.None : new(f.OthersWaiting, f.TrackerPriority, f.IsBug, f.SightingCount, f.StalenessDays),
            [.. (c.Evidence ?? []).Where(e => e.Kind is { Length: > 0 })
                .Select(e => new NextWorkEvidence(e.Kind, e.Summary ?? "", e.Excerpt, e.LooseEndId, e.SessionId))],
            c.TrackerDependent, c.TrackerStateAsOf);
    }
}
