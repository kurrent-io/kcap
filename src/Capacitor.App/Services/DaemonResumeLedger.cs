using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.App.Services;

public static class DaemonResumeLedger {
    /// The agents the daemon's resume ledger holds as resuming, the entries <c>kcap daemon status</c> counts as
    /// "resuming". Throws what the ledger read throws.
    public static IReadOnlySet<string> ResumingAgents(DaemonStore store, string daemonName) =>
        ResumeLedgerStore.For(store, daemonName).ReadAll()
            .Where(entry => entry.State == ResumeState.Resuming)
            .Select(entry => entry.AgentId)
            .ToFrozenSet(StringComparer.Ordinal);
}
