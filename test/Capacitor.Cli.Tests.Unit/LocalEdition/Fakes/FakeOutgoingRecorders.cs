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

    public Task<IReadOnlyList<DaemonBusy>> BusyAsync() {
        log.Add("recorders:busy?");
        return Task.FromResult<IReadOnlyList<DaemonBusy>>(Busy);
    }

    public Task<bool> StopAsync() {
        log.Add("recorders:stop");
        if (StopSucceeds) Alive.Clear();
        return Task.FromResult(StopSucceeds);
    }
}
