using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.Harness.GrokBot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.GrokBot;

/// <summary>
/// Runs the Grok Bot watcher inside the daemon for as long as the profile says to record, so recording
/// starts with the daemon at login and needs no terminal. The setting is re-read while the watcher runs:
/// turning it off stops the watcher, turning it on starts one, neither needing a daemon restart. Never
/// throws — a watcher fault must not take the daemon down.
/// </summary>
/// <param name="record">The profile's current answer; null when the config could not be read, which keeps
/// whatever was last known.</param>
/// <param name="watch">One run of the watcher, until it stops by itself or is cancelled.</param>
internal sealed class GrokBotWatcherService(
        Func<Task<bool?>>                                                     record,
        Func<Action<string>, CancellationToken, Task<GrokBotWatchOutcome>>    watch,
        TimeProvider                                                          time,
        ILogger<GrokBotWatcherService>                                        logger) : BackgroundService {
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    /// <summary>Whether the daemon's profile asks for recording now: null when the config cannot be read.
    /// The watcher posts to the server the daemon started with, so a profile since pointed at another
    /// server gave its answer for that one and it does not count here.</summary>
    public static bool? Records(ProfileConfigLoad outcome, ProfileConfig snapshot, ProfileContext own) {
        if (outcome == ProfileConfigLoad.Unreadable) return null;

        var profile = snapshot.Profiles.GetValueOrDefault(own.Name) ?? own.Effective;

        return profile?.GrokBot?.Record == true && ServerIdentity.SameServer(profile.ServerUrl, own.Resolution.ServerUrl);
    }

    // The last answer actually read, for both loops: an unreadable config must not turn an observed
    // "off" back into the "on" that came before it.
    bool _record;

    protected override async Task ExecuteAsync(CancellationToken ct) {
        GrokBotWatchOutcome? last = null;

        try {
            while (!ct.IsCancellationRequested) {
                if (await RecordAsync().ConfigureAwait(false)) {
                    var outcome = await WatchWhileOnAsync(ct).ConfigureAwait(false);

                    // Said once per stretch: with no chat store or a watcher run by hand, every recheck ends the same way.
                    if (outcome != last && Explain(outcome) is { } why) logger.LogInformation("Grok Bot recording is on but not running: {Reason}", why);
                    last = outcome;
                } else {
                    last = null;
                }

                await Task.Delay(Recheck, time, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One run of the watcher, cancelled when the profile stops asking for it.</summary>
    async Task<GrokBotWatchOutcome> WatchWhileOnAsync(CancellationToken ct) {
        using var run      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var       watching = GuardedAsync(run.Token);

        while (!watching.IsCompleted && !ct.IsCancellationRequested) {
            await Task.WhenAny(watching, Task.Delay(Recheck, time, ct)).ConfigureAwait(false);

            if (!watching.IsCompleted && !ct.IsCancellationRequested && !await RecordAsync().ConfigureAwait(false)) {
                logger.LogInformation("Grok Bot recording turned off; stopping the watcher");
                await run.CancelAsync().ConfigureAwait(false);
            }
        }

        return await watching.ConfigureAwait(false);
    }

    async Task<GrokBotWatchOutcome> GuardedAsync(CancellationToken ct) {
        try {
            return await watch(line => logger.LogInformation("Grok Bot: {Line}", line), ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return GrokBotWatchOutcome.Stopped;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Grok Bot watcher failed; it is started again at the next check");

            return GrokBotWatchOutcome.Failed;
        }
    }

    async Task<bool> RecordAsync() {
        try {
            _record = await record().ConfigureAwait(false) ?? _record;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Grok Bot recording setting could not be read");
        }

        return _record;
    }

    static string? Explain(GrokBotWatchOutcome outcome) => outcome switch {
        GrokBotWatchOutcome.NoSource       => "this computer has no Grok Bot gateway and no Grok Bot desktop app chats",
        GrokBotWatchOutcome.AlreadyRunning => "another Grok Bot watcher on this computer holds the lock",
        _                                  => null
    };
}
