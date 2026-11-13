namespace Capacitor.Cli.Core.Workspace;

/// <summary>An open span's baseline file. <paramref name="Run"/> is the run that opened it, where the vendor's transcript
/// names runs; a file written before it existed, or by a vendor that does not, has none.</summary>
public sealed record WorkspaceSpan(Guid SpanId, string SessionId, string RepoRoot, DateTimeOffset OpenedAt, WorkspaceSnapshot Snapshot, WorkspaceRun? Run = null);
