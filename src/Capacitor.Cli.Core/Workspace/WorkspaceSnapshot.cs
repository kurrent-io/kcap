namespace Capacitor.Cli.Core.Workspace;

/// <summary>What git reports about one working tree at one moment. Reflog lengths of -1 mean the length
/// could not be read, and a span whose either end has one reads no history.</summary>
public sealed record WorkspaceSnapshot {
    public const string Unborn = "unborn";

    public required string Head { get; init; }

    public int HeadReflog { get; init; }

    public int StashReflog { get; init; }

    public required Dictionary<string, WorkspaceEntry> Entries { get; init; }

    public bool Truncated { get; init; }
}
