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
    bool                            Kept,
    string?                         DisconfirmedReason,
    int?                            Rank,
    string                          Because,
    NextWorkFacets                  Facets,
    IReadOnlyList<NextWorkEvidence> Evidence,
    bool                            TrackerDependent = false,
    DateTimeOffset?                 TrackerStateAsOf = null) {
    public bool Disconfirmed => !Kept;

    /// <summary>Null for an outcome the band does not show.</summary>
    public static NextWorkRow? From(NextWorkCandidateDto c) {
        var kept = string.Equals(c.Outcome, "kept", StringComparison.OrdinalIgnoreCase);
        if (!kept && !string.Equals(c.Outcome, "disconfirmed", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(c.TargetKey)) return null;

        var f = c.Facets;
        return new(
            c.TargetKey, c.TargetKind ?? "", c.TargetId, c.TargetLabel ?? "", c.TargetHref, c.RepoHash, c.Arm ?? "", c.Tier, kept,
            c.DisconfirmedReason, c.Rank, c.Because ?? "",
            f is null ? NextWorkFacets.None : new(f.OthersWaiting, f.TrackerPriority, f.IsBug, f.SightingCount, f.StalenessDays),
            [.. (c.Evidence ?? []).Where(e => e.Kind is { Length: > 0 })
                .Select(e => new NextWorkEvidence(e.Kind, e.Summary ?? "", e.Excerpt, e.LooseEndId, e.SessionId))],
            c.TrackerDependent, c.TrackerStateAsOf);
    }
}
