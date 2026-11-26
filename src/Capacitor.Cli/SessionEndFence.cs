using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli;

/// <summary>The hook and watcher entry points to <see cref="ResumeEndFence"/>.</summary>
static class SessionEndFence {
    public static bool ShouldHold(
            string? agentId, string? endingEpoch, string? endingSession, Func<string, ResumeLedgerStore?> locate,
            TimeProvider time, TimeSpan? lockWait = null, bool soft = false) =>
        ResumeEndFence.ShouldHold(agentId, endingEpoch, endingSession, locate, time, soft, lockWait);

    /// <param name="soft">The end is the watcher's idle timeout; see <see cref="ResumeEndFence.ShouldHold(string?, string?, string?, Func{string, ResumeLedgerStore?}, TimeProvider, bool, TimeSpan?)"/>.</param>
    public static bool ShouldHold(HostedAgent hosted, string? endingSession = null, bool soft = false) =>
        ShouldHold(hosted.AgentId, hosted.DaemonEpoch, endingSession, LocateInEnvironmentStore, TimeProvider.System, soft: soft);

    public static bool ShouldHold(ExitedSession exited) => ShouldHold(exited, DaemonStore.FromEnvironment(), TimeProvider.System);

    internal static bool ShouldHold(ExitedSession exited, DaemonStore store, TimeProvider time) =>
        ResumeEndFence.ShouldHold(exited.Origin?.AgentId, exited.Origin?.DaemonEpoch, exited.Session.Value, store, time);

    static ResumeLedgerStore? LocateInEnvironmentStore(string agentId) =>
        ResumeLedgerStore.LocateByAgentId(DaemonStore.FromEnvironment(), agentId);
}
