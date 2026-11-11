namespace Capacitor.Cli.Daemon.Services;

/// The directory names of an agent's borrowed snapshot and scratch workspace. Git and whole source trees
/// run below them, so they carry a short hash of the agent id rather than the id itself: a long id would
/// push those trees past the 260-character Windows path limit. The orphan sweep matches no name, so a
/// directory left under any older naming is still reaped.
internal static class AgentWorkspaceNames {
    public const int KeyLength = 16;

    public static string Key(string agentId) => AgentFileNames.For(agentId)[..KeyLength];

    public static string BorrowedSnapshot(string agentId) => "borrowed-" + Key(agentId);

    public static string Scratch(string agentId) => "scratch-" + Key(agentId);
}
