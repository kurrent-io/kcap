namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>How one run of the watcher behaves. <see cref="Gateway"/> or <see cref="Desktop"/> names the
/// source; with neither, the gateway is used where the computer has one and the desktop app's chats
/// otherwise.</summary>
public sealed record GrokBotWatchOptions(
        string?  Gateway  = null,
        string?  Desktop  = null,
        TimeSpan Interval = default,
        TimeSpan Gap      = default,
        bool     Once     = false,
        bool     DryRun   = false
    ) {
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultGap      = TimeSpan.FromHours(2);

    public TimeSpan Interval { get; init; } = Interval > TimeSpan.Zero ? Interval : DefaultInterval;
    public TimeSpan Gap      { get; init; } = Gap > TimeSpan.Zero ? Gap : DefaultGap;
}
