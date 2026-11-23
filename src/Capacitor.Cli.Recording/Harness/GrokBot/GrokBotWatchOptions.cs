namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>How one run of the watcher behaves. <see cref="GatewayCommand"/>, <see cref="Gateway"/> or
/// <see cref="Desktop"/> names the source; with none, the gateway is used where the computer has one and the
/// desktop app's chats otherwise. <see cref="GatewayCommand"/> reads the gateway descriptor from a command's
/// output rather than a file, for a cloud computer that leaves the descriptor unreadable to a Bot's own user.
/// <see cref="Gap"/> may differ from the default in a dry run only: a session's id comes from where the gap
/// falls, so a run that sends under another gap stores a chat's messages a second time, under other sessions
/// than every other reader of that chat.</summary>
public sealed record GrokBotWatchOptions(
        string?  Gateway        = null,
        string?  GatewayCommand = null,
        string?  Desktop        = null,
        TimeSpan Interval       = default,
        TimeSpan Gap            = default,
        bool     Once           = false,
        bool     DryRun         = false
    ) {
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultGap      = TimeSpan.FromHours(2);

    public TimeSpan Interval { get; init; } = Interval > TimeSpan.Zero ? Interval : DefaultInterval;
    public TimeSpan Gap      { get; init; } = Gap > TimeSpan.Zero ? Gap : DefaultGap;
}
