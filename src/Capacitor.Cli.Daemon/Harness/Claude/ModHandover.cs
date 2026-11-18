namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>A dispatch the server confirmed and the mod was handed: the agent it was sent to, whose input stream is the
/// only one that can take a report about it, and the process that took it.</summary>
internal sealed record ModHandover(string AgentId, string ProcessKey);
