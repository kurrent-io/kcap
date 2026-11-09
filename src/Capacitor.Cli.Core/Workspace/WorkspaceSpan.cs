namespace Capacitor.Cli.Core.Workspace;

/// <summary>An open span's baseline file.</summary>
public sealed record WorkspaceSpan(Guid SpanId, string SessionId, string RepoRoot, DateTimeOffset OpenedAt, WorkspaceSnapshot Snapshot);
