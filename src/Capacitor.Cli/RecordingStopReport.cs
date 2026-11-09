namespace Capacitor.Cli;

/// <summary>What is known of a session's recording once <see cref="WatcherManager.StopRecordingAsync"/> has done what it
/// can. Everything here is something <c>kcap disable</c> has to say out loud.</summary>
/// <param name="Outcome">What the daemons said.</param>
/// <param name="OtherServers">Each server, once, that a daemon which held the session, or hosts its agent, sends to, none of
/// them the one the command deletes from.</param>
/// <param name="AgentLive">A live hosted agent's output goes on reaching the server.</param>
/// <param name="Unreadable">The directory of claims, pid files or daemons couldn't be read, so nothing here knows what
/// records the session.</param>
/// <param name="WatcherLeft">A watcher of the session outlived the attempt to stop it.</param>
/// <param name="DaemonUnmarked">A daemon could not mark the session disabled where it and its agents' hooks read, so they
/// can record it again.</param>
/// <param name="DrainLeft">A pass sending what hooks spooled was still going when the stop stopped waiting, and it may
/// have read the marker before it went down, or one sent a post of the session that never had an answer.</param>
public sealed record RecordingStopReport(
    RecordingStopOutcome Outcome, IReadOnlyList<string> OtherServers, bool AgentLive = false, bool Unreadable = false, bool WatcherLeft = false,
    bool DaemonUnmarked = false, bool DrainLeft = false) {
    /// <summary>Everything that could be recording this session is known to have stopped.</summary>
    public bool Confirmed => this is { Outcome: RecordingStopOutcome.Stopped, Unreadable: false, WatcherLeft: false, DaemonUnmarked: false, DrainLeft: false };
}
