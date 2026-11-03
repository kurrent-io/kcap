namespace Capacitor.Cli.Core.Resume;

/// <summary>
/// Decides, under the agent's ledger-entry lock, whether a hosted session's end is held for a resume.
/// An end that is let through stamps the entry first, so the daemon cannot mark that run for resume
/// once its end may already be on its way to the server.
/// </summary>
public static class ResumeEndFence {
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1);

    public static bool ShouldHold(
            string? agentId, string? endingEpoch, string? endingSession, Func<string, ResumeLedgerStore?> locate,
            TimeProvider time, TimeSpan? lockWait = null) {
        if (agentId is null) return false;

        // A broken ledger must never keep a real session end from posting.
        try {
            if (locate(agentId) is not { } store) return false;

            var hold = false;

            store.TryUpdateUnderLock(agentId, entry => {
                if (entry.State == ResumeState.Resuming || IsEarlierRun(entry, endingEpoch)) {
                    hold = true;
                    return null;
                }

                return entry.EndCommittedAt is null
                    ? entry with { EndCommittedAt = time.GetUtcNow(), EndCommittedEpoch = endingEpoch, EndCommittedSession = endingSession }
                    : null;
            }, lockWait ?? LockWait);

            return hold;
        } catch {
            return false;
        }
    }

    /// <summary>An end that names no agent is placed by its session instead: otherwise the first restart after an
    /// update would end, on the server, every agent it resumed.</summary>
    public static bool ShouldHold(string? agentId, string? endingEpoch, string endingSession, DaemonStore store, TimeProvider time) {
        try {
            agentId ??= ResumeLedgerStore.FindAgentIdBySession(store, endingSession);
        } catch {
            return false;
        }

        return ShouldHold(agentId, endingEpoch, endingSession, id => ResumeLedgerStore.LocateByAgentId(store, id), time);
    }

    /// <summary>A process with no epoch can only be placed once the run has been resumed: every process of
    /// the original run is then from an earlier one.</summary>
    static bool IsEarlierRun(ResumeLedgerEntry entry, string? endingEpoch) =>
        entry.RunEpoch is { } run && (endingEpoch is null ? run != entry.LaunchEpoch : run != endingEpoch);
}
