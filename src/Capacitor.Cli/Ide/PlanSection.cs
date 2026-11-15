namespace Capacitor.Cli.Ide;

/// <param name="State"><c>loading</c>, <c>ready</c>, <c>none</c>, <c>unavailable</c> or <c>signed_out</c>.</param>
/// <param name="Done">Completed and skipped tasks alike.</param>
internal sealed record PlanSection(string State, PlanTask[] Tasks, int Done, int Total) {
    // A record compares arrays by reference, and the host sends a section only when it differs from the one last sent.
    public bool Equals(PlanSection? other) =>
        other is not null && State == other.State && Done == other.Done && Total == other.Total && Tasks.SequenceEqual(other.Tasks);

    public override int GetHashCode() => HashCode.Combine(State, Done, Total);
}
