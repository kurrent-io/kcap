namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>A source a terminal session's hook asks the daemon serving its profile to record. There is no hosted agent to
/// tie it to, so the daemon checks it against what the hook resolved: the daemon's own profile, server and directories,
/// and the session's transcript under the Claude projects of the account the hook resolved.</summary>
/// <param name="SubagentId">Null for the session's own transcript.</param>
/// <param name="SkipTitle">The session has its title already, as a resumed one does.</param>
/// <param name="Profile">The profile the hook resolved.</param>
/// <param name="ServerUrl">The server the hook would send to.</param>
/// <param name="ConfigDir">The hook's kcap config directory, where the markers of a disabled session are.</param>
/// <param name="WatcherDir">Where the hook keeps watcher pid files and recorder claims.</param>
/// <param name="ClaudeHome">The Claude config directory whose projects hold the transcript.</param>
/// <param name="ProcessId">The Claude process running the session, whose exit ends the source.</param>
/// <param name="ProcessToken">That process's start token, so a later process given its pid isn't taken for it.</param>
public sealed record RecordTerminalSourceDto(
    string SessionId, string? SubagentId, string TranscriptPath, string Vendor, string? Cwd, bool SkipTitle,
    string Profile, string ServerUrl, string ConfigDir, string WatcherDir, string ClaudeHome, int ProcessId, string ProcessToken);
