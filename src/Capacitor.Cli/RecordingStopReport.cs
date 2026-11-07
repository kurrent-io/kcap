namespace Capacitor.Cli;

/// <summary>What is known of a session's recording once <see cref="WatcherManager.StopRecordingAsync"/> has done what it
/// can. Everything here is something <c>kcap disable</c> has to say out loud.</summary>
/// <param name="Outcome">What the daemons said.</param>
/// <param name="AgentLive">A live hosted agent's output goes on reaching the server.</param>
/// <param name="OtherServer">The daemon that held the session sends to this server, not the one the command deletes from.</param>
/// <param name="Unreadable">The directory of claims and pid files couldn't be read, so nothing here knows what records the
/// session.</param>
/// <param name="WatcherLeft">A watcher of the session outlived the attempt to stop it.</param>
/// <param name="UnknownHolder">The daemon that held the session, not this profile's, gave no answer, so which server it
/// sends to is unknown.</param>
/// <param name="Earlier">An earlier run left something unconfirmed, ahead of a deletion this run can't see behind.</param>
public sealed record RecordingStopReport(
    RecordingStopOutcome Outcome, bool AgentLive = false, string? OtherServer = null, bool Unreadable = false,
    bool WatcherLeft = false, string? UnknownHolder = null, bool Earlier = false) {
    /// <summary>Everything that could be recording this session is known to have stopped.</summary>
    public bool Confirmed => this is { Outcome: RecordingStopOutcome.Stopped, Unreadable: false, WatcherLeft: false };
}
