namespace Capacitor.Cli.Core.Workspace;

/// <summary>A dirty path in a snapshot: its porcelain code, the path a rename or copy came from, and a
/// stamp of its content, or null when a bound left it unread.</summary>
public sealed record WorkspaceEntry(string Code, string? OriginalPath, string? Hash);
