namespace Capacitor.Cli.Core.Workspace;

/// <summary>A path changed in a span, relative to the span's repository root, as <c>create</c>, <c>edit</c> or <c>delete</c>.</summary>
public sealed record WorkspaceChange(string Path, string ChangeType);
