using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

sealed class OutgoingRecorders(
        DaemonStore store, ConfigRoot config, ProfileContext profiles, UserHome home,
        HarnessRegistry harnesses, BinaryProbe binaries, WatcherManager watchers, TimeProvider time,
        StandardStreams console) : IOutgoingRecorders {
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>The least a probe gets once the shared wait has run out, so a daemon that answers at once still counts
    /// as idle rather than silent.</summary>
    internal static readonly TimeSpan ProbeFloor = TimeSpan.FromMilliseconds(500);

    internal Func<bool> SystemdRunning { get; init; } = ServiceManagerFactory.SystemdRunning;

    /// <summary>Test seam, handed this instance's store; production builds the manager for this OS.</summary>
    internal Func<DaemonStore, IServiceManager>? ServicesFor { get; init; }

    /// <summary>How long the daemons together get to answer. A daemon still starting answers only once it has probed
    /// the vendor CLIs, about 30 s on Windows.</summary>
    internal static readonly TimeSpan StartingWait = TimeSpan.FromSeconds(60);

    static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(2);

    /// <summary>A launch still starting is counted too: it is not in the agent table until its runtime answers. A
    /// daemon too old to give that count counts as busy.</summary>
    public Task<IReadOnlyList<DaemonBusy>> BusyAsync(TimeSpan? wait = null) =>
        BusyAsync(LiveDaemons(), ProbeAsync, time, line => console.Error.WriteLine(line), wait ?? StartingWait);

    async Task<DaemonBusyReason?> ProbeAsync(string name, TimeSpan budget) =>
        (await LocalControlProbe.ProbeAsync(store, name, time, budget)).Snapshot switch {
            null           => DaemonBusyReason.NoAnswer,
            { Busy: null } => DaemonBusyReason.Unknown,
            { Busy: true } => DaemonBusyReason.Running,
            _              => null,
        };

    /// <summary>Probes each daemon, asking a silent one again every <see cref="RetryGap"/> until it answers or the
    /// shared <paramref name="wait"/> runs out. Each probe is given what is left of the wait, at most
    /// <see cref="ProbeBudget"/> and at least <see cref="ProbeFloor"/>. <paramref name="probe"/> gives null for an idle
    /// daemon.</summary>
    internal static async Task<IReadOnlyList<DaemonBusy>> BusyAsync(
            IEnumerable<string> names, Func<string, TimeSpan, Task<DaemonBusyReason?>> probe, TimeProvider time, Action<string> say,
            TimeSpan wait) {
        var busy  = new List<DaemonBusy>();
        var start = time.GetTimestamp();

        TimeSpan Left() => wait - time.GetElapsedTime(start);

        TimeSpan Budget() {
            var left = Left();
            return left < ProbeFloor ? ProbeFloor : left > ProbeBudget ? ProbeBudget : left;
        }

        foreach (var name in names) {
            var reason = await probe(name, Budget());

            if (reason == DaemonBusyReason.NoAnswer && Left() > TimeSpan.Zero) {
                say($"Waiting for daemon '{name}' to finish starting…");

                while (reason == DaemonBusyReason.NoAnswer) {
                    var left = Left();
                    if (left <= TimeSpan.Zero) break;

                    await Task.Delay(left < RetryGap ? left : RetryGap, time);
                    reason = await probe(name, Budget());
                }
            }

            if (reason is { } held) busy.Add(new DaemonBusy(name, held));
        }

        return busy;
    }

    public async Task<bool> StopAsync() {
        var stopped        = true;
        var removedService = false;

        // The service goes first: a stopped daemon whose service is still installed starts again at the next sign-in.
        try {
            var services = ServicesFor?.Invoke(store) ?? ServiceManagerFactory.ForCurrentOs(config, home, time, SystemdRunning, store);

            foreach (var id in services.ListInstalled()) {
                var ended = await ServiceRemoval.EndAgentsAsync(store, id, time);

                if (services.Uninstall(id, out var error)) {
                    if (!ended) DaemonCommands.MarkLedgerInterrupted(store, id, time, console.Error);
                    await console.Out.WriteLineAsync($"Removed daemon service '{id}' ({services.Describe()}).");
                    removedService = true;
                } else {
                    await console.Error.WriteLineAsync($"Could not remove daemon service '{id}': {error}");
                    stopped = false;
                }
            }
        } catch (PlatformNotSupportedException) {
            // Linux without systemd: a unit installed while it ran stays on disk, and would start a deleted binary
            // once systemd runs here again.
            if (OperatingSystem.IsLinux()) {
                IReadOnlyList<string> units;
                try {
                    units = new SystemdServiceManager(home).ListInstalled();
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    await console.Error.WriteLineAsync($"Could not list daemon services: {ex.Message}");
                    stopped = false;
                    units = [];
                }

                foreach (var id in units) {
                    await console.Error.WriteLineAsync(
                        $"Could not remove daemon service '{id}': systemd is not running. Delete {SystemdUnit.UnitPath(home, id)} to finish.");
                    stopped = false;
                }
            }
        }

        // After a service removal the bare "No daemons are running." of `daemon stop` would read as if
        // there had never been one.
        if (!Directory.Exists(store.Directory) || !Directory.EnumerateFiles(store.Directory, "*.pid").Any())
            await console.Out.WriteLineAsync(removedService ? "No other daemons are running." : "No daemons are running.");
        else if (await new DaemonCommands(store, config, profiles, home, harnesses, binaries, time, console).HandleAsync(["daemon", "stop", "--yes"]) != 0)
            stopped = false;

        if (await new CleanupCommand(watchers, console).HandleCleanup() != 0) stopped = false;

        foreach (var survivor in Live()) {
            await console.Error.WriteLineAsync($"{survivor} is still running.");
            stopped = false;
        }

        return stopped;
    }

    public string WatcherDirectory => watchers.GetWatcherDir();

    public IReadOnlyList<string> Live() => [
        .. LiveDaemons().Select(n => $"Daemon '{n}'"),
        .. LiveWatchers().Select(k => $"Watcher {k}"),
    ];

    IEnumerable<string> LiveDaemons() =>
        Directory.Exists(store.Directory)
            ? Directory.EnumerateFiles(store.Directory, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(n => n.Length > 0 && DaemonPidProbe.ValidatedPid(store, n) is not null)
                .Order()
            : [];

    IEnumerable<string> LiveWatchers() {
        var dir = watchers.GetWatcherDir();

        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(k => k.Length > 0 && watchers.PidAlive(k))
                .Order()
            : [];
    }
}
