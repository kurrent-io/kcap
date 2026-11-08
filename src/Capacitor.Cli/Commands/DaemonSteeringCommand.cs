using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon steering on|off</c> flips the steering switch of the current profile's daemon, through that daemon:
/// it is the only writer of the consent document the switch lives in. <c>kcap daemon steering-socket</c> prints the
/// socket the steering mod talks to, or nothing; the mod runs it to find the daemon, so stdout carries the path alone.
/// </summary>
public static class DaemonSteeringCommand {
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    const string NotOffered =
        "kcap daemon steering: steering isn't offered on Windows yet, because the steering mod's socket transport "
      + "hasn't been checked there.";

    public static Task<int> SetAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args) =>
        SetAsync(store, profiles, time, console, args, SteeringIpc.Offered);

    internal static async Task<int> SetAsync(
            DaemonStore store, ProfileContext profiles, TimeProvider time, StandardStreams console, string[] args, bool offered) {
        if (!offered) {
            await console.Error.WriteLineAsync(NotOffered);

            return 1;
        }

        if (WithoutName(args) is not [(SteeringIpc.On or SteeringIpc.Off) and var state]) {
            await console.Error.WriteLineAsync("Usage: kcap daemon steering <on|off> [--name <n>]");

            return 1;
        }

        if (await ResolveNameAsync(profiles, console, args) is not { } name) return 1;

        using var timeout = new CancellationTokenSource(ReplyTimeout, time);
        var ack = await SteeringIpc.SetAsync(store, name, state == SteeringIpc.On, timeout.Token);

        if (ack.Ok) {
            await console.Out.WriteLineAsync($"Steering is {ack.Steering} for daemon '{name}'.");

            return 0;
        }

        await console.Error.WriteLineAsync($"kcap daemon steering: {Explain(ack, name)}");

        return 1;
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

    static string Explain(SteeringSetAckDto ack, string name) => ack.Reason switch {
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
