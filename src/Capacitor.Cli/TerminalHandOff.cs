using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>Which daemon a hook outside any hosted agent hands a session to, and what it tells that daemon: what it
/// resolved, for the daemon to hold against its own. The session's process and transcript layout are read only once the
/// daemon has said it takes terminal sessions.</summary>
/// <param name="daemonName">The daemon this hook's profile names, as <c>kcap daemon</c> resolves it.</param>
/// <param name="claudeHomeOf">The Claude config directory a transcript belongs to.</param>
/// <param name="processOf">The process a source follows, given the one a caller names or null for the coding agent this
/// hook runs under; null when there is none to follow.</param>
public sealed class TerminalHandOff(
        string daemonName, string profile, string? serverUrl, string configDir, string watcherDir,
        Func<string, string> claudeHomeOf, Func<int?, ProcessIncarnation?> processOf) {
    public string DaemonName => daemonName;

    /// <summary>Null when the daemon has nothing to end the source with, or nothing to send it to: the hook then starts a
    /// watcher, which has the same server and its own parent monitor.</summary>
    public RecordTerminalSourceDto? Describe(RecordingSource source, bool skipTitle, int? follows) {
        if (serverUrl is not { Length: > 0 } url || processOf(follows) is not { } process) return null;

        return new RecordTerminalSourceDto(
            source.SessionId, source.AgentId, source.TranscriptPath, source.Vendor, source.Cwd, skipTitle,
            profile, url, configDir, watcherDir, claudeHomeOf(source.TranscriptPath), process.Pid, process.StartToken);
    }
}
