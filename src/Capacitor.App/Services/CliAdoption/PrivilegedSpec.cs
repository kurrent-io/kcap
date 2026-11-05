namespace Capacitor.App.Services.CliAdoption;

/// <summary>Everything the privileged helper may touch, fixed before the administrator prompt. The status file and the
/// control pipe share one folder, which must be this user's own, mode 0700, or the helper refuses to start.
/// <paramref name="GoWait"/> bounds its wait for go.</summary>
public sealed record PrivilegedSpec(
    IReadOnlyList<LinkChange> Links, IReadOnlyList<PrivilegedEntry> Entries, string ControlFifo, string StatusFile, TimeSpan GoWait);
