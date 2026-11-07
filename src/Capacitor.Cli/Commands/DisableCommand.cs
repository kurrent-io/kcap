using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap disable</c>: a person is told recording has stopped and their data is gone, so the order matters and
/// the output says what it couldn't confirm. The marker goes down first, because whatever would start a recorder reads
/// it. The deletion comes last, because a send that lands behind it can bring rows back. Exits 1 when it can't say that
/// recording stopped with nothing on its way.</summary>
class DisableCommand(ConfigRoot config, WatcherManager watchers) {
    /// <param name="sessions">Resolved only once recording has stopped: a profile with no usable server still gets that far.</param>
    public async Task<int> Handle(string sessionId, Func<ISessionsApi> sessions, TextWriter stdout, TextWriter stderr) {
        DisabledSessions.Mark(sessionId, config);

        var stopped   = await watchers.StopRecordingAsync(sessionId, stderr);
        var confirmed = stopped is not RecordingDisable.Unanswered;

        try {
            switch (await sessions().DeleteSessionAsync(sessionId)) {
                case DeleteSessionResponse.Deleted:
                    await stdout.WriteLineAsync(confirmed
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
                await SayWhatCouldStillLandAsync(sessionId, stopped, stdout);

                return 1;
            }

            await stdout.WriteLineAsync(confirmed
                ? "Session disabled locally (watcher stopped, hooks silenced). Server data not deleted."
                : "Session disabled locally (hooks silenced). Server data not deleted.");
        }

        await SayWhatCouldStillLandAsync(sessionId, stopped, stdout);

        return stopped is RecordingDisable.Stopped ? 0 : 1;
    }

    static async Task SayWhatCouldStillLandAsync(string sessionId, RecordingDisable stopped, TextWriter stdout) {
        var caveat = stopped switch {
            RecordingDisable.SendInFlight => "The server had not answered a send from just before recording stopped, so it may still reach the server.",
            RecordingDisable.Unanswered   => "The daemon recording this session did not confirm it stopped, so more may reach the server.",
            _                             => null,
        };

        if (caveat is not null) await stdout.WriteLineAsync($"{caveat} To delete what lands, run: kcap disable {sessionId}");
    }
}
