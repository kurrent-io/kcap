namespace Capacitor.Cli.Commands;

/// <param name="Version">The version the daemon recorded at startup, when it recorded one.</param>
/// <param name="Starting">Answering, but still probing its vendors.</param>
public sealed record StatusDaemonEntryJson(string Name, int Pid, string? Version = null, bool Starting = false);
