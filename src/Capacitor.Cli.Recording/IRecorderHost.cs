using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Titles;

namespace Capacitor.Cli.Recording;

/// <summary>What a recorder's housekeeping needs that only the process hosting it can do. A host with nothing to
/// offer answers each member in kind: null for a store, an empty list for usage rows,
/// <see cref="CommitObservation.Uncovered"/> for a commit observation. Where a member returns a bool, false is
/// not a way to decline: <see cref="StartChildAsync"/>'s result is ignored by most scans, and
/// <see cref="FinishGrokChildAsync"/>'s false means the caller retries.</summary>
public interface IRecorderHost {
    /// <summary>Starts the watches that can stop the run. Called once the recorder has connected: a stop before
    /// then skips its final drain.</summary>
    void ArmProcessWatches();

    /// <summary>Stops the run, on the calling thread.</summary>
    void StopRun();

    /// <summary>A generated title can't go out over this connection, so something outliving the recorder has
    /// to finish it.</summary>
    void HandOffTitle(string sessionId, string vendor, string transcriptPath);

    /// <summary>Records <paramref name="child"/> unless something already does.</summary>
    /// <returns>Whether a recorder is running for it now.</returns>
    Task<bool> StartChildAsync(RecordingSource child);

    /// <summary>Stops the child source <paramref name="key"/> names, after its final drain.</summary>
    /// <returns>Whether nothing is left running for it.</returns>
    Task<bool> StopChildAsync(string key);

    /// <summary>Stops and drains a Grok subagent, then posts its stop.</summary>
    /// <returns>Whether the stop is settled: accepted, or refused for good. Otherwise the caller retries.</returns>
    Task<bool> FinishGrokChildAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType);

    /// <summary>The store for a vendor that keeps a session's title in a SQLite database. This assembly can't read
    /// one itself: the AOT daemon references it, and SQLite's native bundle must never reach that binary.</summary>
    IHarnessTitleStore? OpenSqliteTitleStore(string vendor, string transcriptPath);

    /// <summary>Usage rows of an Antigravity conversation database after row <paramref name="afterIdx"/>.</summary>
    IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt);

    /// <summary>The commit observation a recorder starts with; <see cref="CommitObservation.Uncovered"/> when no
    /// git hook files this session's commits.</summary>
    Task<CommitObservation> ObserveCommitsAsync(string sessionId, string? agentId, string? cwd);

    /// <summary>Whether kcap's git hook still files this session's commits.</summary>
    Task<bool> CoversCommitsAsync(string sessionId, string? cwd);
}
