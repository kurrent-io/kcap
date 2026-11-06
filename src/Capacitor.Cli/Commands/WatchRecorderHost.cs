using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap watch</c> as a recorder's host: one recorder in its own process, with a child source's
/// recorder running as a separate watcher.</summary>
/// <param name="armProcessWatches">Starts this process's watches on the agent.</param>
/// <param name="stop">Cancels the run.</param>
sealed class WatchRecorderHost(
        ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses, ICapacitorHttpClient http,
        WatcherManager watchers, TimeProvider time, Action armProcessWatches, Action stop) : IRecorderHost {
    public void ArmProcessWatches() => armProcessWatches();

    public void StopRun() => stop();

    public void HandOffTitle(string sessionId, string vendor, string transcriptPath) =>
        watchers.SpawnTitleGenerator(sessionId, vendor, transcriptPath);

    public async Task<bool> StartChildAsync(RecordingSource child) {
        await watchers.EnsureWatcherRunning(key: child.Key, transcriptPath: child.TranscriptPath,
            agentId: child.AgentId, sessionIdOverride: child.SessionId, cwd: child.Cwd, vendor: child.Vendor);

        // EnsureWatcherRunning returns normally when it could not start one.
        return watchers.IsWatcherAlive(child.Key);
    }

    public Task<bool> StopChildAsync(string key) => watchers.TryStopWatcher(key);

    public Task<bool> FinishGrokChildAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType) =>
        new GrokSubagentTeardown(profiles, http, watchers, time).FinishAsync(sessionId, parentTranscript, childSessionId, agentType);

    public IHarnessTitleStore? OpenSqliteTitleStore(string vendor, string transcriptPath) => vendor switch {
        "antigravity" => AntigravitySummaryTitle.ForTranscript(transcriptPath),
        "cursor"      => CursorComposerTitle.ForTranscript(harnesses.Of<CursorHarness>().Paths, transcriptPath),
        _             => null,
    };

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) =>
        AntigravityGenMetadataDb.ReadUsageLines(dbPath, afterIdx, createdAt);

    public Task<CommitObservation> ObserveCommitsAsync(string sessionId, string? agentId, string? cwd) =>
        GitHook.ObservationAsync(config, sessionId, agentId, cwd, time);

    public Task<bool> CoversCommitsAsync(string sessionId, string? cwd) =>
        GitHook.CoversAsync(config, sessionId, cwd, time);
}
