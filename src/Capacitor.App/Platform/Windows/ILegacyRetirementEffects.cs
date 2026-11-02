namespace Capacitor.App.Platform.Windows;

/// The machine changes a legacy retirement makes. Each throws on failure; <see cref="LegacyInstallRetirement"/> logs it and
/// decides what may still run.
public interface ILegacyRetirementEffects {
    /// False when the process is still running. One that already exited, or whose pid now names another process, is
    /// not touched and counts as stopped.
    bool EndProcess(RunningImage process);

    /// Null once no daemon task runs a binary inside <paramref name="legacyRoot"/>, otherwise why one still does.
    Task<string?> RepointDaemonsAsync(string legacyRoot, CancellationToken ct);

    void DeleteShortcut(string path);

    void DeleteAutostart(string valueName);

    void RepointUninstallEntry(string legacyRoot, string ownRoot);

    void MoveUserPath(string fromCli, string toCli);

    /// Moves the root aside and deletes it; throws, leaving it in place, when anything in it is in use.
    void RemoveRoot(string root);

    /// Deletes the root's updater, so a root that could not be removed stops updating itself.
    void RemoveUpdater(string root);

    /// Deletes the entry while it still names <paramref name="root"/>.
    void DeleteUninstallEntry(string key, string root);
}
