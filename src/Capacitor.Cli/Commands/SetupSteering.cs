using Capacitor.Cli.Core;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Setup's steering question, whose answer defaults to the switch as it stands: off unless the daemon has it on. A yes
/// takes the path <c>kcap daemon steering on</c> takes, for the daemon setup just configured, and a no while it is on
/// the path <c>kcap daemon steering off</c> takes. Not offered on Windows.
/// </summary>
static class SetupSteering {
    internal const string Flag = "--steering";

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    internal const string Explanation =
        "Terminal steering lets you send messages from the kcap web and mobile apps to Claude Code sessions running in "
      + "your own terminal. Turning it on installs a small Claude Code plugin, once.";

    internal const string Question = "Turn on terminal steering?";

    internal const string LeftOff = "Terminal steering: off (`kcap daemon steering on` turns it on).";

    internal const string LeftOn = "Terminal steering: on (`kcap daemon steering off` turns it off).";

    /// <summary>Refuses a <c>--steering</c> value before anything changes.</summary>
    internal static string? ArgumentError(string[] args, bool offered) {
        if (!args.Contains(Flag)) return null;

        return FlagValue(args) switch {
            null               => $"{Flag} needs on or off.",
            true when !offered => $"{Flag}: {DaemonSteeringCommand.NotOfferedReason}",
            _                  => null,
        };
    }

    /// <summary>Whether steering could reach a session after this run: a yes needs a daemon to flip the switch and
    /// kcap's Claude plugin for the steering plugin to go in beside.</summary>
    internal static bool Eligible(FirstRunMachineActionResult? daemonService, ClaudeSteeringMod mod) =>
        daemonService is { } service && SetupDaemonService.IsUp(service) && mod.KcapPluginInstalled();

    /// <param name="eligible">Whether to ask; see <see cref="Eligible"/>. A <c>--steering</c> answer is honoured either way.</param>
    /// <param name="ask">Asks the question with the given default; null when this run asks nothing.</param>
    /// <returns>Whether steering is on now.</returns>
    internal static async Task<bool> RunAsync(
            string[] args, bool offered, bool eligible, Func<bool, bool>? ask,
            DaemonStore store, string daemonName, TimeProvider time, ClaudeSteeringMod mod, Action<string> writeLine) {
        if (!offered) return false;

        var flag = args.Contains(Flag) ? FlagValue(args) : null;

        // The switch as the running daemon has it: a socket listens only while it is on.
        bool wasOn;
        using (var probe = new CancellationTokenSource(ProbeTimeout, time))
            wasOn = await SteeringIpc.ListeningSocketAsync(store, daemonName, probe.Token) is not null;

        bool? wanted;

        if (ask is not null && eligible) {
            writeLine(Explanation);
            wanted = ask(flag ?? wasOn);
        } else {
            wanted = flag;
        }

        if (wanted is not { } on) {
            if (eligible) writeLine(wasOn ? LeftOn : LeftOff);

            return wasOn;
        }

        if (!on && !wasOn) return false;

        var (ack, install) = await DaemonSteeringCommand.TurnAsync(store, daemonName, on, time, mod);

        if (!ack.Ok) {
            writeLine(on
                ? $"Steering is still off: {DaemonSteeringCommand.Explain(ack, daemonName)} Turn it on later with `kcap daemon steering on`."
                : $"Steering is still on: {DaemonSteeringCommand.Explain(ack, daemonName)} Turn it off later with `kcap daemon steering off`.");

            return !on;
        }

        writeLine($"Steering is {(on ? "on" : "off")} for daemon '{daemonName}'.");
        foreach (var note in install.Notes) writeLine(note);
        foreach (var failure in install.Failures) writeLine(char.ToUpperInvariant(failure[0]) + failure[1..]);

        return on;
    }

    static bool? FlagValue(string[] args) {
        var i = Array.IndexOf(args, Flag);

        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? ProviderApiKeyPolicy.TryParseBool(args[i + 1]) : null;
    }
}
