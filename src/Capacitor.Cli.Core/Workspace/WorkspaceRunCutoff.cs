namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// The spans one run of a session owns, for an end that must not close a later run's: those opened in the run named
/// <paramref name="RunId"/>. A span from before spans named their run, or an end that could not read its run, falls back to
/// the spans opened by <paramref name="OpenedBy"/>. A span whose run was unreadable is left to the next run's start, which
/// replaces it, by any end that knows its own run.
/// </summary>
public sealed record WorkspaceRunCutoff(string? RunId, DateTimeOffset? OpenedBy) {
    public bool Covers(WorkspaceSpan span) =>
        RunId is { } runId && span.Run is { } run
            ? run.Id == runId
            : OpenedBy is not { } openedBy || span.OpenedAt <= openedBy;
}
