namespace Capacitor.Cli.Core.Workspace;

public enum WorkspaceOpen {
    /// <summary>A new turn: every span the session still has open is closed first.</summary>
    Turn,
    /// <summary>The turn moved into another tree: its spans stay open, and a tree that already has one keeps it.</summary>
    Join,
    /// <summary>A new run of a session whose previous run ended: like <see cref="Turn"/>, and a baseline that close left
    /// behind is replaced rather than carried on, so the earlier run's later close cannot take this run's changes.</summary>
    Run,
}
