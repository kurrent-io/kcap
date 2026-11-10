using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>A host that records what it is asked. Children start and stop unless a test says otherwise, and
/// it has no SQLite reader or git hook.</summary>
public sealed class FakeRecorderHost : IRecorderHost {
    readonly List<RecordingSource> _started = [];
    readonly List<string>          _stopped = [];

    public int Armed { get; private set; }

    /// <summary>The thread each <see cref="StopRun"/> ran on.</summary>
    public List<int> StopThreads { get; } = [];

    public List<(string SessionId, string Vendor, string TranscriptPath)> TitlesHandedOff { get; } = [];

    public Func<RecordingSource, bool> StartsChild { get; set; } = _ => true;

    public Func<string, bool> StopsChild { get; set; } = _ => true;

    public Func<string, string, IHarnessTitleStore?> SqliteTitleStore { get; set; } = (_, _) => null;

    /// <summary>The children asked to start, oldest first.</summary>
    public IReadOnlyList<RecordingSource> Started {
        get {
            lock (_started) return [.._started];
        }
    }

    /// <summary>The keys asked to stop, oldest first.</summary>
    public IReadOnlyList<string> Stopped {
        get {
            lock (_stopped) return [.._stopped];
        }
    }

    public void ArmProcessWatches() => Armed++;

    public void StopRun() => StopThreads.Add(Environment.CurrentManagedThreadId);

    public void HandOffTitle(string sessionId, string vendor, string transcriptPath) =>
        TitlesHandedOff.Add((sessionId, vendor, transcriptPath));

    public Task<bool> StartChildAsync(RecordingSource child) {
        lock (_started) _started.Add(child);

        return Task.FromResult(StartsChild(child));
    }

    public Task<bool> StopChildAsync(string key) {
        lock (_stopped) _stopped.Add(key);

        return Task.FromResult(StopsChild(key));
    }

    public Task<bool> FinishGrokChildAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType) =>
        Task.FromResult(false);

    public IHarnessTitleStore? OpenSqliteTitleStore(string vendor, string transcriptPath) => SqliteTitleStore(vendor, transcriptPath);

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) => [];

    public Task<CommitObservation> ObserveCommitsAsync(string sessionId, string? agentId, string? cwd) =>
        Task.FromResult<CommitObservation>(new CommitObservation.Uncovered());

    public Task<bool> CoversCommitsAsync(string sessionId, string? cwd) => Task.FromResult(false);

    public int WorkspaceDrains { get; private set; }

    public Task DrainWorkspaceAsync() {
        WorkspaceDrains++;

        return Task.CompletedTask;
    }
}
