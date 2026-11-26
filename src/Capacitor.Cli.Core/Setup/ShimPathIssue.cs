namespace Capacitor.Cli.Core.Setup;

public enum ShimPathIssue {
    Unknown,
    InvalidTarget,
    UnsupportedShell,
    ExistingCommand,
    LinkFailed,
    LinkFolderOccupied,
    ShellProfileNotUpdated,
    FolderMissingFromPath,
    AnotherCommandFirst,
    LinkUnverified,
    UserPathNotUpdated,
    UserPathUnverified
}
