namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The client floor of a local installation, for the CLI, the daemon and the desktop app alike: a kcap must be newer
/// than <see cref="NewestRefused"/>. An older copy does not know the local rules, so it would post past the
/// destination check and spool where nothing drains. The local server refuses by the same number, so the two must
/// move together.
/// </summary>
public static class LocalClientFloor {
    /// <summary>The newest release that does not keep to the local rules. Every build after it, prereleases
    /// included, is admitted.</summary>
    public const string NewestRefused = "1.3.0";

    /// <summary>A version that cannot be read is not admitted.</summary>
    public static bool Admits(string? version) => PrereleaseSemver.Compare(version, NewestRefused) > 0;

    /// <summary>The newest release with no local edition for Windows. A copy up to it keeps to the local rules, so the
    /// server admits its hooks, but its <c>kcap local</c> says the edition runs on macOS only, so on Windows a kcap
    /// that comes first on PATH must be newer.</summary>
    public const string NewestRefusedOnWindows = "1.4.0";

    /// <summary>The floor for a kcap on a machine, by <see cref="AdmitsOn"/>.</summary>
    public static string NewestRefusedOn(bool windows) => windows ? NewestRefusedOnWindows : NewestRefused;

    /// <summary>Whether a kcap on a machine can run the local edition there: <see cref="Admits"/>, and on Windows newer
    /// than <see cref="NewestRefusedOnWindows"/>.</summary>
    public static bool AdmitsOn(bool windows, string? version) =>
        Admits(version) && (!windows || PrereleaseSemver.Compare(version, NewestRefusedOnWindows) > 0);
}
