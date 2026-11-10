using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Recording;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>What one recorder asks of the daemon. A hosted Claude session has a transcript, no child scan and no SQLite
/// store, so the members for those answer in kind. SQLite's native bundle must never reach this binary.</summary>
/// <param name="hookCommand">The command kcap's git hook names, from the CLI beside this binary; null when there is
/// none, so no commit counts as covered.</param>
/// <param name="stop">Finishes this recorder's source.</param>
/// <param name="handOffTitle">Starts the <c>kcap</c> command that finishes a title, given the session, the vendor and
/// the transcript's path.</param>
internal sealed class DaemonRecorderHost(
        ConfigRoot config, string? hookCommand, TimeProvider time, Action stop, Action<string, string, string> handOffTitle, Action<string> log,
        CommandRunner? run = null) : IRecorderHost {
    // The daemon hears of its agent's exit through the runtime, and the recorder's stop comes from there.
    public void ArmProcessWatches() { }

    public void StopRun() => stop();

    public void HandOffTitle(string sessionId, string vendor, string transcriptPath) => handOffTitle(sessionId, vendor, transcriptPath);

    public Task<bool> StartChildAsync(RecordingSource child) {
        log($"A scan asked for a recorder for {child.Key}, and the daemon starts none for a scan");

        return Task.FromResult(false);
    }

    public Task<bool> StopChildAsync(string key) => Task.FromResult(true);

    public Task<bool> FinishGrokChildAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType) => Task.FromResult(true);

    public IHarnessTitleStore? OpenSqliteTitleStore(string vendor, string transcriptPath) => null;

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) => [];

    public Task<CommitObservation> ObserveCommitsAsync(string sessionId, string? agentId, string? cwd) =>
        hookCommand is null
            ? Task.FromResult<CommitObservation>(new CommitObservation.Uncovered())
            : GitHookProbe.ObservationAsync(config, sessionId, agentId, cwd, Claimed, hookCommand, time, run);

    public Task<bool> CoversCommitsAsync(string sessionId, string? cwd) =>
        hookCommand is null ? Task.FromResult(false) : GitHookProbe.CoversAsync(sessionId, cwd, Claimed, hookCommand, time, run);

    // The daemon's spool drain loop delivers the workspace spool on its own cadence.
    public Task DrainWorkspaceAsync() => Task.CompletedTask;

    // A hosted session runs under the agent process this daemon started, whatever its hooks have got round to claiming.
    static bool Claimed(SessionId session) => true;
}
