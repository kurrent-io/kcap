namespace Capacitor.App.Services.Update;

/// Takes the daemon down before an updater that would otherwise end it mid-turn.
public interface IUpdateDaemonStop {
    /// <param name="waiting">Told how long at most the daemon holds the stop for its agents' turns, when it has to.</param>
    /// <param name="ct">Withdraws a stop the daemon still holds.</param>
    Task<DaemonHandoff> StopAsync(Action<TimeSpan> waiting, CancellationToken ct);

    /// The update did not go ahead: the daemon is the app's to keep running again.
    void Release();
}
