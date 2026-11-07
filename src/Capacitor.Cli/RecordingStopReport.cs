using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli;

/// <summary>What is known of a session's recording once <see cref="WatcherManager.StopRecordingAsync"/> has done what it
/// can. Everything here is something <c>kcap disable</c> has to say out loud.</summary>
/// <param name="Outcome"><see cref="RecordingDisable.Stopped"/>, or what keeps that from being said.</param>
/// <param name="TerminalLive">A live hosted agent's remote terminal goes on reaching the server.</param>
/// <param name="OtherServer">The daemon that held the session sends to this server, not the one the command deletes from.</param>
/// <param name="Unreadable">The directory of claims and pid files couldn't be read, so nothing here knows what records the
/// session.</param>
public sealed record RecordingStopReport(RecordingDisable Outcome, bool TerminalLive = false, string? OtherServer = null, bool Unreadable = false);
