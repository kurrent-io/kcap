namespace Capacitor.Cli.Core.Workspace;

public enum WorkspaceOpen {
    /// <summary>A new turn: every span the session still has open is closed first.</summary>
    Turn,
    /// <summary>The turn moved into another tree: its spans stay open, and a tree that already has one keeps it.</summary>
    Join,
}
