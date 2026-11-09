using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap disable</c>: a person is told recording has stopped and their data is gone, so the order matters and
/// every line says only what this run knows. The marker goes down first, because whatever would start a recorder reads
/// it. The deletion comes last, because a send that lands behind it can bring rows back. Exits 1 when something stays
/// unconfirmed, and 0 with the server's data untouched when the server can't be reached: the local steps are done, and
/// the line says what wasn't.</summary>
class DisableCommand(ConfigRoot config, WatcherManager watchers, HostedAgent hosted) {
    const string AgentGoesOn = "This agent is still running, and its live output keeps reaching the server until it ends. Stop the agent to end it.";

    const string EarlierDeletion =
        "An earlier kcap disable deleted this session before it could confirm recording had stopped. Anything that landed after "
      + "that deletion may still be on the server, and kcap can't tell or remove it from here; the server's administrator can check.";

    /// <summary>Where the session this runs inside is read from.</summary>
    internal Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;

    /// <param name="sessions">Resolved only once recording has stopped: a profile with no usable server still gets that far.</param>
    public async Task<int> Handle(string sessionId, Func<ISessionsApi> sessions, TextWriter stdout, TextWriter stderr) {
        var earlier    = DisabledSessions.WasDeletedUnconfirmed(sessionId, config);
        var elsewhere  = DisabledSessions.OtherServers(sessionId, config);
        var markFailed = Failure(() => DisabledSessions.Mark(sessionId, config));
        var marked     = markFailed is null;

        var stopped   = await watchers.StopRecordingAsync(sessionId, stderr);
        var live      = stopped.AgentLive || RunsHostedAgent(sessionId);
        var confirmed = marked && stopped.Confirmed;

        // "Recording stopped" is earned: everything that could record answered, nothing's output goes on, and no other
        // server holds what this deletion can't reach.
        var sure    = confirmed && !live && stopped.OtherServers.Count == 0;
        var caveats = Caveats(stopped, markFailed);
        var exit    = 0;

        foreach (var other in stopped.OtherServers) {
            if (Failure(() => DisabledSessions.MarkOnOtherServer(sessionId, other, config)) is { } unkept)
                caveats.Add($"kcap could not record that a daemon sends this session to {other} ({unkept}), so a later kcap disable won't name that server.");
        }

        try {
            switch (await sessions().DeleteSessionAsync(sessionId)) {
                case DeleteSessionResponse.Deleted:
                    // After the deletion, which a send still on its way can land behind, and ahead of a line that may not print.
                    if (!confirmed && Failure(() => DisabledSessions.MarkDeletedUnconfirmed(sessionId, config)) is { } unrecorded)
                        caveats.Add($"kcap could not record that this deletion ran before recording was confirmed stopped ({unrecorded}), so a later kcap disable won't say so.");

                    await stdout.WriteLineAsync(!marked ? $"Server data for session {sessionId} deleted."
                        : sure ? $"Session {sessionId} disabled. Recording stopped and server data deleted."
                        : $"Session {sessionId} disabled. Server data deleted.");

                    break;
                case DeleteSessionResponse.NotFound:
                    await stdout.WriteLineAsync(marked
                        ? $"Session {sessionId} disabled. No server data found (may have already been deleted)."
                        : $"No server data found for session {sessionId} (may have already been deleted).");

                    break;
            }
        } catch (CapacitorApiException ex) {
            await stderr.WriteLineAsync(ex.Message);

            // Local state has already changed, so a server that was never reached is not a failure.
            if (ex.Status is not null) exit = 1;
            else await stdout.WriteLineAsync(Local(marked, sure));
        } catch (UnusableServerUrlException ex) {
            await stderr.WriteLineAsync(ex.Message);
            await stdout.WriteLineAsync(Local(marked, sure));

            exit = 2;
        }

        if (earlier) caveats.Add(EarlierDeletion);

        // This run's own lines already name the servers it found.
        foreach (var server in elsewhere) {
            if (!stopped.OtherServers.Any(found => DisabledSessions.SameServer(server, found)))
                caveats.Add($"An earlier kcap disable found a daemon sending this session to {server}. This command can't delete data there; run kcap disable under the profile for that server.");
        }

        foreach (var caveat in caveats) await stdout.WriteLineAsync(caveat);

        if (live) await stdout.WriteLineAsync(AgentGoesOn);

        return exit != 0 ? exit : caveats.Count == 0 ? 0 : 1;
    }

    static string Local(bool marked, bool sure) =>
        !marked ? "Server data not deleted."
      : sure ? "Session disabled locally (recording stopped, hooks silenced). Server data not deleted."
      : "Session disabled locally (hooks silenced). Server data not deleted.";

    // The daemon can't see an agent that moved to this session when no hand-over reached it, or answer at all; a hook
    // wrote the move into the agent's claim. Inside the agent, the environment names its own session.
    bool RunsHostedAgent(string sessionId) =>
        SessionId.Parse(sessionId) is { } session && AgentSessions.OnThisMachine(config).RunsHostedAgent(session)
     || hosted.AgentId is not null && ArgParsing.ResolveSessionIdFromEnv(GetEnv) == sessionId;

    static List<string> Caveats(RecordingStopReport stopped, string? markFailed) {
        var caveats = new List<string>();

        if (markFailed is not null) caveats.Add($"kcap could not mark this session disabled ({markFailed}), so a hook or a restart can record it again.");

        if (stopped.Unreadable) caveats.Add("kcap could not read what records this session, so it may still be recording.");

        if (stopped.WatcherLeft) caveats.Add("A watcher recording this session could not be stopped, so it may still be sending.");

        if (stopped.DaemonUnmarked)
            caveats.Add("A kcap daemon could not mark this session disabled where its agents' hooks read it, so they can record it again.");

        if (stopped.DrainLeft)
            caveats.Add("A pass sending what hooks had spooled was still running when kcap stopped waiting, so a send of this session may still land.");

        caveats.AddRange(stopped.Outcome switch {
            RecordingStopOutcome.HolderUnconfirmed => ["The daemon recording this session did not confirm it stopped, so more may have reached the server."],
            RecordingStopOutcome.DaemonSilent      => ["A kcap daemon on this machine did not answer, so if it hosts this session, more may have reached the server."],
            RecordingStopOutcome.StillStopping     => ["The daemon was still stopping this session's recording when kcap stopped waiting, so more may reach the server."],
            RecordingStopOutcome.SendInFlight      => ["A batch already on its way when recording stopped had no answer from the server, so it may still have landed."],
            _                                      => [],
        });

        foreach (var other in stopped.OtherServers)
            caveats.Add($"A daemon that recorded this session or hosts its agent sends to {other}, and nothing was deleted there. Run kcap disable under the profile for that server.");

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
