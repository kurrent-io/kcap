using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>One candidate of <see cref="NextWorkSnapshotDto"/>. Labels, because-clauses and evidence are tracker- or
/// model-authored text: show them as text, never as markup or a command.</summary>
public sealed record NextWorkCandidateDto(
    [property: JsonPropertyName("target_key")]          string                   TargetKey,
    [property: JsonPropertyName("target_kind")]         string                   TargetKind,
    [property: JsonPropertyName("target_id")]           string?                  TargetId,
    [property: JsonPropertyName("target_label")]        string?                  TargetLabel,
    [property: JsonPropertyName("target_href")]         string?                  TargetHref,
    [property: JsonPropertyName("repo_hash")]           string?                  RepoHash,
    [property: JsonPropertyName("arm")]                 string?                  Arm,
    [property: JsonPropertyName("tier")]                int                      Tier,
    [property: JsonPropertyName("outcome")]             string?                  Outcome,
    [property: JsonPropertyName("disconfirmed_reason")] string?                  DisconfirmedReason,
    [property: JsonPropertyName("rank")]                int?                     Rank,
    [property: JsonPropertyName("because")]             string?                  Because,
    [property: JsonPropertyName("facets")]              NextWorkFacetsDto?       Facets,
    [property: JsonPropertyName("evidence")]            List<NextWorkEvidenceDto>? Evidence,
    [property: JsonPropertyName("tracker_dependent")]   bool                     TrackerDependent = false,
    [property: JsonPropertyName("tracker_state_as_of")] DateTimeOffset?          TrackerStateAsOf = null);
