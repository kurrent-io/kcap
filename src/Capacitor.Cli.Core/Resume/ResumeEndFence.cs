namespace Capacitor.Cli.Core.Resume;

/// <summary>
/// Decides, under the agent's ledger-entry lock, whether a hosted session's end is held for a resume.
/// An end that is let through stamps the entry first, so the daemon cannot mark that run for resume
/// once its end may already be on its way to the server.
/// </summary>
public static class ResumeEndFence {
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1);

    /// <param name="soft">The end is the watcher's idle timeout, which the session can outlive: a later turn of it lets
    /// the daemon clear the stamp. A real end stamped over a soft one replaces it, so a turn never clears a real end.</param>
    /// <remarks>The stamp describes one session's end. An end of the session the entry now runs replaces a stamp an earlier
    /// session of the same process left (a Claude <c>/clear</c>); a late end of that earlier session never replaces the
    /// current one's.</remarks>
    public static bool ShouldHold(
            string? agentId, string? endingEpoch, string? endingSession, Func<string, ResumeLedgerStore?> locate,
            TimeProvider time, bool soft = false, TimeSpan? lockWait = null) {
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

                return Replaces(entry, endingSession, soft)
                    ? entry with {
                        EndCommittedAt      = time.GetUtcNow(),
                        EndCommittedEpoch   = endingEpoch,
                        EndCommittedSession = endingSession,
                        EndCommittedSoft    = soft ? true : null,
                    }
                    : null;
            }, lockWait ?? LockWait);

            return hold;
        } catch {
            return false;
        }
    }

    /// <summary>Whether an end of <paramref name="endingSession"/> takes the entry's stamp: there is none, the ending session
    /// is the one the entry runs and the stamp names another, or the stamp is the same session's soft end and this one is real.</summary>
    static bool Replaces(ResumeLedgerEntry entry, string? endingSession, bool soft) {
        if (entry.EndCommittedAt is null) return true;

        var stamped = SessionIds.Canonical(entry.EndCommittedSession);
        var ending  = SessionIds.Canonical(endingSession);

        if (stamped is null || ending is null || string.Equals(stamped, ending, StringComparison.OrdinalIgnoreCase))
            return entry.EndCommittedSoft == true && !soft;

        // An entry that has not learned its session yet cannot tell the two apart; the newer end is the likelier current one.
        return SessionIds.Canonical(entry.SessionId) is not { } current || string.Equals(ending, current, StringComparison.OrdinalIgnoreCase);
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
