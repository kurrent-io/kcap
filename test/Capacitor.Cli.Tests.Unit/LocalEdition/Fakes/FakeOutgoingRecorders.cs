using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>Daemons and watchers as an uninstall sees them; each call appends its name to the shared log.</summary>
sealed class FakeOutgoingRecorders(List<string> log, string watcherDirectory = "/watchers") : IOutgoingRecorders {
    public string WatcherDirectory { get; set; } = watcherDirectory;

    public List<DaemonBusy> Busy { get; } = [];

    public bool StopSucceeds { get; set; } = true;

    /// <summary>What <see cref="Live"/> reports; a successful stop empties it.</summary>
    public List<string> Alive { get; } = [];

    public IReadOnlyList<string> Live() {
        log.Add("recorders:live?");
        return [.. Alive];
    }

    /// <summary>The wait the last <see cref="BusyAsync"/> was given.</summary>
    public TimeSpan? BusyWait { get; private set; }

    public Task<IReadOnlyList<DaemonBusy>> BusyAsync(TimeSpan? wait = null) {
        log.Add("recorders:busy?");
        BusyWait = wait;
        return Task.FromResult<IReadOnlyList<DaemonBusy>>(Busy);
    }

    public Task<bool> StopAsync() {
        log.Add("recorders:stop");
        if (StopSucceeds) Alive.Clear();
        return Task.FromResult(StopSucceeds);
    }
}
