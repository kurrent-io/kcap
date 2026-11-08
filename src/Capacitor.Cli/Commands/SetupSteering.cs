using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Setup's steering question, asked once the daemon service is up and off unless the person says yes. A yes takes
/// the path <c>kcap daemon steering on</c> takes, for the daemon setup just configured. Not offered on Windows.
/// </summary>
static class SetupSteering {
    internal const string Flag = "--steering";

    internal const string Explanation =
        "Terminal steering lets you send messages from the kcap web and mobile apps to Claude Code sessions running in "
      + "your own terminal. Turning it on installs a small Claude Code plugin, once.";

    internal const string Question = "Turn on terminal steering?";

    internal const string LeftOff = "Terminal steering: off (`kcap daemon steering on` turns it on).";

    /// <summary>Refuses a <c>--steering</c> value before anything changes.</summary>
    internal static string? ArgumentError(string[] args, bool offered) {
        if (!args.Contains(Flag)) return null;

        return FlagValue(args) switch {
            null               => $"{Flag} needs on or off.",
            true when !offered => $"{Flag}: {DaemonSteeringCommand.NotOfferedReason}",
            _                  => null,
        };
    }

    /// <param name="ask">Asks the question with the given default; null when this run asks nothing.</param>
    /// <returns>Whether steering is on now.</returns>
    internal static async Task<bool> RunAsync(
            string[] args, bool offered, bool claudeDetected, Func<bool, bool>? ask,
            DaemonStore store, string daemonName, TimeProvider time, ClaudeSteeringMod mod, Action<string> writeLine) {
        if (!offered) return false;

        var flag = args.Contains(Flag) ? FlagValue(args) : null;
        bool on;

        if (ask is null) {
            on = flag ?? false;
            if (!on && claudeDetected) writeLine(LeftOff);
        } else if (claudeDetected) {
            writeLine(Explanation);
            on = ask(flag ?? false);
        } else {
            on = flag ?? false;
        }

        if (!on) return false;

        var (ack, install) = await DaemonSteeringCommand.TurnAsync(store, daemonName, on: true, time, mod);

        if (!ack.Ok) {
            writeLine($"Steering is still off: {DaemonSteeringCommand.Explain(ack, daemonName)} Turn it on later with `kcap daemon steering on`.");

            return false;
        }

        writeLine($"Steering is on for daemon '{daemonName}'.");
        if (install.Installed is { } installed) writeLine(installed);
        if (install.Failed is { } failed) writeLine(char.ToUpperInvariant(failed[0]) + failed[1..]);

        return true;
    }

    static bool? FlagValue(string[] args) {
        var i = Array.IndexOf(args, Flag);

        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? ProviderApiKeyPolicy.TryParseBool(args[i + 1]) : null;
    }
}
