using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap disable</c>: a person is told recording has stopped and their data is gone, so the order matters and
/// the output says what it couldn't confirm. The marker goes down first, because whatever would start a recorder reads
/// it. The deletion comes last, because a send that lands behind it can bring rows back. Exits 1 when it can't say that
/// recording stopped with nothing on its way. A second run has no claims left to go on, so what an earlier run couldn't
/// confirm stays unconfirmed, and running this again is no cure for a send that landed: its deletion can answer 404 with
/// the stream revived.</summary>
class DisableCommand(ConfigRoot config, WatcherManager watchers) {
    const string AgentGoesOn = "This agent is still running, and its live output keeps reaching the server until it ends. Stop the agent to end it.";

    /// <param name="sessions">Resolved only once recording has stopped: a profile with no usable server still gets that far.</param>
    public async Task<int> Handle(string sessionId, Func<ISessionsApi> sessions, TextWriter stdout, TextWriter stderr) {
        var earlier    = DisabledSessions.IsUnconfirmed(sessionId, config);
        var markFailed = Failure(() => DisabledSessions.Mark(sessionId, config));

        var stopped = await watchers.StopRecordingAsync(sessionId, stderr);
        var caveats = Caveats(stopped, earlier, markFailed);
        var sure    = stopped is { Outcome: not RecordingDisable.Unanswered, Unreadable: false } && !earlier;
        var exit    = caveats.Count == 0 ? 0 : 1;

        if (markFailed is null && (earlier || stopped.Outcome is not RecordingDisable.Stopped || stopped.Unreadable)) Failure(() => DisabledSessions.MarkUnconfirmed(sessionId, config));

        try {
            switch (await sessions().DeleteSessionAsync(sessionId)) {
                case DeleteSessionResponse.Deleted:
                    await stdout.WriteLineAsync(sure
                        ? $"Session {sessionId} disabled. Recording stopped and server data deleted."
                        : $"Session {sessionId} disabled. Server data deleted.");

                    break;
                case DeleteSessionResponse.NotFound:
                    await stdout.WriteLineAsync($"Session {sessionId} disabled. No server data found (may have already been deleted).");

                    break;
            }
        } catch (CapacitorApiException ex) {
            await stderr.WriteLineAsync(ex.Message);

            // Local state has already changed, so a server that was never reached is not a failure.
            if (ex.Status is not null) {
                exit = 1;
            } else {
                await stdout.WriteLineAsync(sure
                    ? "Session disabled locally (watcher stopped, hooks silenced). Server data not deleted."
                    : "Session disabled locally (hooks silenced). Server data not deleted.");
            }
        }

        foreach (var caveat in caveats) await stdout.WriteLineAsync(caveat);

        if (stopped.AgentLive) await stdout.WriteLineAsync(AgentGoesOn);

        return exit;
    }

    static List<string> Caveats(RecordingStopReport stopped, bool earlier, string? markFailed) {
        var caveats = new List<string>();

        if (markFailed is not null) caveats.Add($"kcap could not mark this session disabled ({markFailed}), so a hook or a restart can record it again.");

        if (stopped.Unreadable) caveats.Add("kcap could not read what records this session, so it may still be recording.");
        else if (stopped.Outcome is RecordingDisable.Unanswered) caveats.Add("The daemon recording this session did not confirm it stopped, so more may have reached the server.");

        if (stopped.Outcome is RecordingDisable.SendInFlight) caveats.Add("A batch already on its way when recording stopped had no answer from the server, so it may still have landed.");

        if (earlier && caveats.Count == 0) caveats.Add("An earlier kcap disable could not confirm this session stopped recording, and this run has nothing new to go on.");

        if (stopped.OtherServer is { } other) caveats.Add($"The daemon that held this session sends to {other}, and nothing was deleted there. Run kcap disable under the profile for that server.");

        return caveats;
    }

    // Null when it worked. A marker that can't be written is no reason to leave recording running.
    static string? Failure(Action write) {
        try {
            write();

            return null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return ex.Message;
        }
    }
}
