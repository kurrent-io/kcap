namespace Capacitor.Cli.Core.Workspace;

/// <summary>The changes of one span, sorted by path, one per path.</summary>
public sealed record WorkspaceDetection(List<WorkspaceChange> Changes, bool Truncated);
