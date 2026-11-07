using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// What the attached daemon last reported it was running.
/// <param name="Busy">Any agent running or starting, an idle one included.</param>
/// <param name="Quiet">Nothing it reports is mid-work, as <see cref="QuietIn"/> reads it.</param>
public sealed record DaemonActivity(int ActiveAgents, bool Busy, bool Quiet = false) {
    /// <summary>No launch is starting and every live agent waits on its user, with no question on its screen and no
    /// subagent running; a daemon that cannot keep its agents across a restart is quiet only with none at all. Anything
    /// the daemon leaves out, or a status this app does not know, counts against it.</summary>
    /// <param name="keepsAgents">Whether the daemon keeps its agents for a successor to resume.</param>
    public static bool QuietIn(DaemonStatusDto snapshot, bool keepsAgents) {
        if (snapshot.Pending is not { Count: 0 }) return false;

        var live = snapshot.Agents.Where(a => a.Status is not ("Interrupted" or "Completed" or "Failed")).ToList();
        // Busy also counts a launch admitted before either list shows it.
        if (live.Count == 0) return snapshot.Busy is not true;

        return keepsAgents && live.All(a => a is { Status: "Running", AwaitingInput: true, TerminalDialog: null, LiveSubagents: null or 0 });
    }
}
