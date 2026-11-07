namespace Capacitor.Cli.Core.Workspace;

/// <summary>The body of <c>POST /hooks/workspace-changes</c>, and of a workspace spool file.</summary>
public sealed record WorkspaceChangesRequest(
    string SessionId,
    Guid SpanId,
    string RepoRoot,
    DateTimeOffset SpanOpenedAt,
    DateTimeOffset ObservedAt,
    List<WorkspaceChange> Changes,
    bool Truncated);
