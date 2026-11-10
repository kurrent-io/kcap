namespace Capacitor.App.Services.CliAdoption;

/// <summary>A kcap install the app did not make and may take over. <paramref name="Root"/> is the npm launcher's
/// package folder or the script install's root; <paramref name="OwnedEntries"/> is everything deleting the install
/// removes; <paramref name="Identity"/> is the <see cref="EntryIdentity"/> of <paramref name="DaemonBinary"/>.</summary>
public sealed record OtherCliInstall(
    OtherCliInstallKind Kind, string Root, string Version, string CliBinary, string DaemonBinary, string Identity,
    IReadOnlyList<string> OwnedEntries);
