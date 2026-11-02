namespace Capacitor.App.Platform.Windows;

/// One step of retiring a legacy install, in the order <see cref="LegacyRetirementPlan"/> puts them.
public abstract record LegacyRetirementStep {
    LegacyRetirementStep() { }

    /// Ends the legacy updater and app, each only while it is still the process the scan saw.
    public sealed record StopProcesses(IReadOnlyList<RunningImage> Processes) : LegacyRetirementStep;

    /// Points every daemon task that runs a binary inside <c>From</c> at this install's daemon.
    public sealed record RepointDaemons(string From) : LegacyRetirementStep;

    public sealed record RemoveShortcuts(IReadOnlyList<string> Paths) : LegacyRetirementStep;

    public sealed record RemoveAutostart(IReadOnlyList<string> ValueNames) : LegacyRetirementStep;

    /// Rewrites the <c>kcap</c> uninstall entry from the legacy root to this install.
    public sealed record RepointUninstallEntry(string From, string To) : LegacyRetirementStep;

    /// Swaps the legacy CLI's folder on the user PATH for this install's.
    public sealed record MoveUserPath(string FromCli, string ToCli) : LegacyRetirementStep;

    public sealed record RemoveRoot(string Root) : LegacyRetirementStep;

    /// Entries other than <c>kcap</c> that name the legacy root, removed once its updater is gone.
    public sealed record RemoveUninstallEntries(string Root, IReadOnlyList<string> Keys) : LegacyRetirementStep;
}
