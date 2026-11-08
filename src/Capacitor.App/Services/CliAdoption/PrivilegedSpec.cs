namespace Capacitor.App.Services.CliAdoption;

/// <summary>Everything the privileged helper may touch, fixed before the administrator prompt. The status file and the
/// control pipe share one folder, which must be this user's own, mode 0700, or the helper refuses to start.
/// <paramref name="GoWait"/> bounds its wait for go. <paramref name="Staging"/> is random hex the helper puts in the
/// name it makes each new link under before renaming it over the old one, so nobody can plant anything there in
/// advance. <paramref name="Homes"/> holds every account's home, whose launch agents the helper reads.</summary>
public sealed record PrivilegedSpec(
    IReadOnlyList<LinkChange> Links, IReadOnlyList<PrivilegedEntry> Entries, string ControlFifo, string StatusFile, TimeSpan GoWait,
    string Staging, string Homes = "/Users") {
    /// <summary>What can name an install below a home, which the helper reads in every account's.</summary>
    public HomeSites Sites { get; init; } = HomeSites.None;

    /// <summary>The install's paths as a JSON or XML file may store them, which the helper looks for besides the paths
    /// themselves.</summary>
    public IReadOnlyList<string> Needles { get; init; } = [];
}
