namespace Capacitor.App.Services.CliAdoption;

/// <summary>A daemon service file, with what launchd says about its job. <paramref name="KcapOnStoredPath"/> is the
/// first <c>kcap</c> on the <c>PATH</c> the plist stores, as found.</summary>
public sealed record ServiceUnitFact(
    string PlistPath, string Name, string Program, string? StoredPath, string? KcapOnStoredPath,
    bool Loaded, int? JobPid, string? LoadedProgram);

public sealed record RunningDaemonFact(string Name, int Pid, string? StartToken, string? Executable);

/// <summary>Everything read about other kcap installs on this Mac. <paramref name="KcapOnLoginPath"/> is the first
/// <c>kcap</c> on <paramref name="LoginPath"/>, as found.</summary>
public sealed record AdoptionEvidence(
    IReadOnlyList<OtherCliInstall> Installs, IReadOnlyList<ServiceUnitFact> Units, IReadOnlyList<RunningDaemonFact> Daemons,
    string? LoginPath, string? KcapOnLoginPath) {
    /// <summary>Every <c>kcap</c> found in the folders a kcap link is made in, whichever PATH finds it first.</summary>
    public IReadOnlyList<string> Links { get; init; } = [];
}
