namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// The spans one run of a session owns, for an end that must not close a later run's: those opened at or below its
/// transcript run boundary. Where the transcript could not be read, those opened by a time instead. A span recorded with no
/// boundary belongs to the run being ended.
/// </summary>
public sealed record WorkspaceRunCutoff(long? Boundary, DateTimeOffset? OpenedBy) {
    public bool Covers(WorkspaceSpan span) =>
        Boundary is { } boundary ? span.RunBoundary is not { } opened || opened <= boundary
      : OpenedBy is not { } openedBy || span.OpenedAt <= openedBy;
}
