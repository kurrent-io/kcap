using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.GrokBot;
using Capacitor.Cli.Recording.Harness.GrokBot;

namespace Capacitor.Cli.Commands.Harness;

/// <summary><c>kcap grok-bot watch</c>: the Grok Bot watcher run by hand, logging to stderr. The daemon runs
/// the same watcher itself for a profile that sets <c>grok_bot.record</c>. <c>kcap grok-bot status</c> says
/// whether one is running on this computer, whoever started it, where and as which profile it posts, and
/// what its last cycle met.</summary>
public sealed class GrokBotWatchCommand(GrokBotWatcher watcher, UserHome home, TimeProvider time) {
    public async Task<int> HandleAsync(string[] args) {
        if (args is [_, "status"]) {
            await Console.Out.WriteLineAsync(GrokBotWatchStatus.Describe(watcher.Running()));
            return 0;
        }

        if (args.Length < 2 || args[1] != "watch") {
            await Console.Error.WriteLineAsync("Usage: kcap grok-bot watch [--once] [--dry-run] [--gateway <gateway.json> | --desktop <dir>] [--interval <seconds>] [--gap-minutes <n>]");
            await Console.Error.WriteLineAsync("       kcap grok-bot status");
            return 1;
        }

        var options = new GrokBotWatchOptions(
            Gateway:  Option(args, "--gateway"),
            Desktop:  Option(args, "--desktop"),
            Interval: int.TryParse(Option(args, "--interval"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : default,
            Gap:      int.TryParse(Option(args, "--gap-minutes"), out var g) && g > 0 ? TimeSpan.FromMinutes(g) : default,
            Once:     args.Contains("--once"),
            DryRun:   args.Contains("--dry-run"));

        using var cts = new CancellationTokenSource();

        void Cancel(object? _, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }

        Console.CancelKeyPress += Cancel;

        try {
            switch (await watcher.RunAsync(options, Log, cts.Token)) {
                case GrokBotWatchOutcome.NoSource:
                    await Console.Error.WriteLineAsync(
                        $"No Grok Bot here: no gateway at {watcher.GatewayDescriptor}, and no desktop app chats at {GrokBotDesktopPaths.ChatStore(home) ?? "(no application data directory)"}. "
                      + "Run this on a Grok Bot cloud computer or on a computer with the Grok Bot desktop app, or pass --gateway or --desktop.");
                    return 1;

                case GrokBotWatchOutcome.AlreadyRunning:
                    await Console.Error.WriteLineAsync(
                        "A Grok Bot watcher is already running on this computer: another `kcap grok-bot watch`, or the kcap daemon when grok_bot.record is on.");
                    return 1;

                case GrokBotWatchOutcome.Failed:
                    return 1;

                default:
                    return 0;
            }
        } finally {
            Console.CancelKeyPress -= Cancel;
        }
    }

    static string? Option(string[] args, string name) {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void Log(string message) => Console.Error.WriteLine($"{time.GetUtcNow():O} {message}");
}
