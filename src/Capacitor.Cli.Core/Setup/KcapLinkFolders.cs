namespace Capacitor.Cli.Core.Setup;

/// The folders a `kcap` command link is installed into, most system-wide first.
public static class KcapLinkFolders {
    public static IReadOnlyList<string> For(UserHome home) => [
        "/usr/local/bin",
        "/opt/homebrew/bin",
        Path.Combine(home.Path, ".local", "bin"),
        Path.Combine(home.Path, "bin")
    ];

    /// <see cref="For"/> plus the folders of every link the adopted-link inventory records.
    public static IReadOnlyList<string> WithAdopted(UserHome home, ConfigRoot config) =>
        [.. For(home).Concat(new AdoptedLinkInventory(config).Links().Select(l => Path.GetDirectoryName(l)!).Where(f => f.Length > 0)).Distinct(StringComparer.Ordinal)];
}
