namespace Capacitor.App.Services.Update;

/// What became of the daemon before an update handed over to the updater.
public enum DaemonHandoff {
    /// Nothing was asked of it: the updater leaves the daemon running.
    None,

    /// It stopped, keeping its agents for the next daemon, once their turns ended or its turn wait ran out.
    Stopped,

    /// No daemon was running.
    NotRunning,

    /// It did not stop by itself, so the updater ends it with the app and cuts off any turn still running.
    Interrupted,
}
