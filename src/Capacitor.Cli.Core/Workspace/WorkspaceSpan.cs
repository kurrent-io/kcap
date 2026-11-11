namespace Capacitor.Cli.Core.Workspace;

/// <summary>An open span's baseline file. <paramref name="RunBoundary"/> names the run that opened it, where the vendor's
/// transcript marks runs; a file written before it existed has none.</summary>
public sealed record WorkspaceSpan(Guid SpanId, string SessionId, string RepoRoot, DateTimeOffset OpenedAt, WorkspaceSnapshot Snapshot, long? RunBoundary = null);
