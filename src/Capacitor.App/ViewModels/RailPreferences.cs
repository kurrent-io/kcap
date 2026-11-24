using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The rail's remembered shape for one user: the controls, the group keys they folded and the project
/// they narrowed to, null for all projects, the session ids and work item keys removed from the rail, and the work items
/// created from it, which it lists before they have a session.
/// Value-equal on the folded and removed keys as sequences, so a persisted copy compares equal to the live one.
public sealed record RailPreferences(
        RailScope Scope, RailState State, RailGrouping Grouping, RailSort Sort, bool Descending,
        IReadOnlyList<string> Collapsed, RailProject? Project = null, IReadOnlyList<string>? Removed = null,
        IReadOnlyList<string>? Created = null) {
    public static RailPreferences Default => new(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.LastUpdated, true, []);

    public bool Equals(RailPreferences? other) =>
        other is not null && Scope == other.Scope && State == other.State && Grouping == other.Grouping
        && Sort == other.Sort && Descending == other.Descending && Collapsed.SequenceEqual(other.Collapsed, StringComparer.Ordinal)
        && Equals(Project, other.Project) && (Removed ?? []).SequenceEqual(other.Removed ?? [], StringComparer.Ordinal)
        && (Created ?? []).SequenceEqual(other.Created ?? [], StringComparer.Ordinal);

    public override int GetHashCode() {
        var hash = HashCode.Combine(Scope, State, Grouping, Sort, Descending, Collapsed.Count, Project);
        foreach (var key in Collapsed) hash = HashCode.Combine(hash, key);
        foreach (var key in Removed ?? []) hash = HashCode.Combine(hash, key);
        foreach (var id in Created ?? []) hash = HashCode.Combine(hash, id);
        return hash;
    }
}
