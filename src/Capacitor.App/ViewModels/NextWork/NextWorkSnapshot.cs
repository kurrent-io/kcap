using System.Collections.Frozen;
using Capacitor.Remote.Models;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>One engine run as the band reads it.</summary>
public sealed record NextWorkSnapshot(
    IReadOnlyList<NextWorkRow>          Candidates,
    bool                                Partial,
    bool                                Incomplete,
    IReadOnlyDictionary<string, string> RepoNames) {
    public static readonly NextWorkSnapshot Empty = new([], false, false, FrozenDictionary<string, string>.Empty);

    public static NextWorkSnapshot From(NextWorkSnapshotDto dto) => new(
        [.. (dto.Candidates ?? []).Select(NextWorkRow.From).OfType<NextWorkRow>()],
        dto.Partial,
        dto.Incomplete,
        dto.Repos is { Count: > 0 } repos ? repos.ToFrozenDictionary(StringComparer.Ordinal) : FrozenDictionary<string, string>.Empty);
}
