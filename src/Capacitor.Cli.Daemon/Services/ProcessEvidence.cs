namespace Capacitor.Cli.Daemon.Services;

/// <summary>A process as its pid and the start identity recorded when it was spawned, kept past its runtime's
/// disposal.</summary>
internal readonly record struct ProcessEvidence(int Pid, string Identity);
