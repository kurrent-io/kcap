namespace Capacitor.App.Services.CliAdoption;

/// <summary>Everything the privileged helper may touch, fixed before the administrator prompt; each entry's aside is the
/// name <see cref="AsideRetirement.Plan"/> recorded. A directory entry's aside must hold <paramref name="Marker"/>, a
/// path relative to it, as a regular file containing every one of <paramref name="MarkerContains"/>; a link or file
/// entry must still be a link or file. <paramref name="GoWait"/> bounds the helper's wait for go.</summary>
public sealed record PrivilegedSpec(
    IReadOnlyList<LinkChange> Links, IReadOnlyList<AsideEntry> Entries, string Marker, IReadOnlyList<string> MarkerContains,
    string ControlFifo, string StatusFile, TimeSpan GoWait);
