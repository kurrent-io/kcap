namespace Capacitor.App.Services.LocalSetup;

/// What <see cref="StaleClientRepair"/> left behind; anything but Clear and Repaired keeps the install from starting.
public abstract record ClientRepairOutcome {
    ClientRepairOutcome() { }

    /// Nothing on this computer was below the floor.
    public sealed record Clear : ClientRepairOutcome;

    public sealed record Repaired(string Path) : ClientRepairOutcome;

    /// The administrator prompt was dismissed.
    public sealed record Cancelled(string Path) : ClientRepairOutcome;

    public sealed record Failed(string Path, string Reason) : ClientRepairOutcome;

    /// Another copy of the desktop app is below the floor; only the user can update or remove an app.
    public sealed record OldApp(string Path) : ClientRepairOutcome;

    /// An older kcap on PATH that npm could not update and Windows has no administrator relink for: only the user
    /// can update or remove it.
    public sealed record Unrepairable(string Path) : ClientRepairOutcome;

    /// npm updated the kcap on PATH, but its newest is still one that cannot run the local edition here.
    public sealed record StillOld(string Path, string? Version) : ClientRepairOutcome;

    public bool Ready => this is Clear or Repaired;
}
