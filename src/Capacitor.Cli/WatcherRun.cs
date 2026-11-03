namespace Capacitor.Cli;

/// <summary>The hosted agent and daemon epoch of the run a watcher follows, when it is not the run of the process
/// that spawns it: the watcher's end and its resume fence are judged as that run's.</summary>
public sealed record WatcherRun(string? AgentId, string? DaemonEpoch);
