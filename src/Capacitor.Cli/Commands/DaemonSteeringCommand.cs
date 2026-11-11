using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon steering on|off</c> flips the steering switch of the current profile's daemon, through that daemon:
/// it is the only writer of the consent document the switch lives in. <c>kcap daemon steering-socket</c> prints the
/// socket the steering mod talks to, or nothing; the mod runs it to find the daemon, so stdout carries the path alone.
/// </summary>
public static class DaemonSteeringCommand {
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    internal const string NotOfferedReason =
        "steering isn't offered on Windows yet, because the steering mod's socket transport hasn't been checked there.";

    internal static Task<int> SetAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args, ClaudeSteeringMod mod) =>
        SetAsync(store, profiles, time, console, args, mod, SteeringIpc.Offered);

    internal static async Task<int> SetAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args,
            ClaudeSteeringMod mod, bool offered) {
        if (!offered) {
            await console.Error.WriteLineAsync($"kcap daemon steering: {NotOfferedReason}");

            return 1;
        }

        if (WithoutName(args) is not [(SteeringIpc.On or SteeringIpc.Off) and var state]) {
            await console.Error.WriteLineAsync("Usage: kcap daemon steering <on|off> [--name <n>]");

            return 1;
        }

        if (await ResolveNameAsync(profiles, console, args) is not { } name) return 1;

        // The mod finds its daemon through `steering-socket` with no name, so another daemon would never be reached.
        if (state == SteeringIpc.On && DaemonNameResolver.Resolve([], profiles.DaemonName) is var found && found != name) {
            await console.Error.WriteLineAsync(
                $"kcap daemon steering: the steering plugin finds this profile's daemon, '{found}', so it would never reach daemon '{name}'. "
              + $"Turn steering on from the profile whose daemon '{name}' is, or leave out --name.");

            return 1;
        }

        var (ack, install) = await TurnAsync(store, name, state == SteeringIpc.On, time, mod);

        if (!ack.Ok) {
            await console.Error.WriteLineAsync($"kcap daemon steering: {Explain(ack, name)}");

            return 1;
        }

        await console.Out.WriteLineAsync($"Steering is {ack.Steering} for daemon '{name}'.");
        foreach (var note in install.Notes) await console.Out.WriteLineAsync(note);
        foreach (var failure in install.Failures) await console.Error.WriteLineAsync($"kcap daemon steering: {failure}");

        return install.Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The daemon flips the switch and the CLI then installs the mod, because the daemon never writes Claude's
    /// settings. Flipping first keeps a refused flip from touching them, and a failed install from holding the switch.</summary>
    internal static async Task<(SteeringSetAckDto Ack, ClaudeSteeringModInstall Install)> TurnAsync(
            DaemonStore store, string name, bool on, TimeProvider time, ClaudeSteeringMod mod) {
        using var timeout = new CancellationTokenSource(ReplyTimeout, time);
        var ack = await SteeringIpc.SetAsync(store, name, on, timeout.Token);

        return (ack, ack.Ok && on ? mod.InstallOnce() : ClaudeSteeringModInstall.None);
    }

    public static Task<int> PrintSocketAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args) =>
        PrintSocketAsync(store, profiles, time, console, args, SteeringIpc.Offered);

    internal static async Task<int> PrintSocketAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args, bool offered) {
        if (!offered) return 0;

        if (await ResolveNameAsync(profiles, console, args) is not { } name) return 1;

        using var timeout = new CancellationTokenSource(ProbeTimeout, time);
        if (await SteeringIpc.ListeningSocketAsync(store, name, timeout.Token) is { } path) await console.Out.WriteLineAsync(path);

        return 0;
    }

    internal static string Explain(SteeringSetAckDto ack, string name) => ack.Reason switch {
        SteeringIpc.Unreachable => $"daemon '{name}' is not running (start it with `kcap daemon start -d --name {name}`).",
        SteeringIpc.Unsupported => $"daemon '{name}' doesn't support steering; restart it on this version with `kcap daemon restart --name {name}`.",
        SteeringIpc.Unanswered  => $"daemon '{name}' did not answer.",
        SteeringIpc.Unwritable  => $"daemon '{name}' could not save the switch, which is still {ack.Steering}{Why(ack)}",
        SteeringIpc.Unbindable  => $"daemon '{name}' could not open its steering socket, so the switch is still {ack.Steering}{Why(ack)}",
        SteeringIpc.NotOffered  => $"daemon '{name}' runs where steering isn't offered.",
        _                       => $"daemon '{name}' refused the request ({ack.Reason}).",
    };

    static string Why(SteeringSetAckDto ack) => ack.Detail is { Length: > 0 } detail ? $": {detail}" : "; its log says why.";

    static async Task<string?> ResolveNameAsync(ProfileContext profiles, StandardStreams console, string[] args) {
        try {
            return DaemonNameResolver.Resolve(args, profiles.DaemonName);
        } catch (ArgumentException ex) {
            await console.Error.WriteLineAsync(ex.Message);

            return null;
        }
    }

    static string[] WithoutName(string[] args) {
        List<string> rest = [];

        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--name") {
                i++;

                continue;
            }

            rest.Add(args[i]);
        }

        return [.. rest];
    }
}
