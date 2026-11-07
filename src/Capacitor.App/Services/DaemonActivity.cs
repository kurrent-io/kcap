using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// What the attached daemon last reported it was running.
/// <param name="Busy">Any agent running or starting, an idle one included.</param>
/// <param name="Quiet">Nothing it reports is mid-work, as <see cref="QuietIn"/> reads it.</param>
public sealed record DaemonActivity(int ActiveAgents, bool Busy, bool Quiet = false) {
    /// <summary>No launch is starting and every live agent waits on its user, with no question on its screen and no
    /// subagent running. Anything the daemon leaves out, or a status this app does not know, counts against it.</summary>
    public static bool QuietIn(DaemonStatusDto snapshot) =>
        snapshot.Pending is { Count: 0 } && snapshot.Agents.All(a => a.Status switch {
            "Running"                                => a is { AwaitingInput: true, TerminalDialog: null, LiveSubagents: null or 0 },
            "Interrupted" or "Completed" or "Failed" => true,
            _                                        => false,
        });
}
