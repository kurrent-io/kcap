namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>The watcher running on this computer, as it described itself when it started.
/// <see cref="Server"/> is null when that description cannot be read or was left by a process that is
/// gone: a watcher is running, and where it posts is unknown.</summary>
public sealed record GrokBotWatchStatus(string? Server, int? Pid);
