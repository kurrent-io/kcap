using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Windows;

/// <summary>
/// A pre-rebrand Kurrent Capacitor install: a Velopack root named <c>KurrentCapacitor</c>. It follows kcap's update
/// feed and updates itself into the kcap package in place, so beside a kcap install it keeps running, sharing the
/// daemon's Scheduled Task, the daemon store and the <c>kcap</c> uninstall entry, until the kcap install removes it.
/// </summary>
public sealed record LegacyWindowsInstall(string Root, string? Version) {
    public const string FolderName = "KurrentCapacitor";
    public const string KcapFolderName = "kcap";

    /// <summary>The legacy install the kcap install at <paramref name="ownRoot"/> replaces, or null. One newer than
    /// <paramref name="ownVersion"/> is left until this install catches up: taking its task would downgrade the daemon.</summary>
    /// <param name="registered">The install locations the uninstall entries name.</param>
    public static LegacyWindowsInstall? Find(string ownRoot, string? ownVersion, string? localAppData, IEnumerable<string?> registered) {
        if (IsLegacy(ownRoot)) return null;
        var candidates = Candidates(ownRoot, localAppData, FolderName)
            .Concat(registered.OfType<string>().Where(location => location.Length > 0 && IsLegacy(location)))
            .Where(root => !LegacyPaths.Same(root, ownRoot))
            .DistinctBy(LegacyPaths.Normalize, StringComparer.OrdinalIgnoreCase);
        foreach (var root in candidates) {
            if (VelopackRoot.AppIn(root) is null) continue;
            var version = VelopackRoot.Version(root);
            if (PrereleaseSemver.IsNewer(version, ownVersion)) continue;
            return new LegacyWindowsInstall(LegacyPaths.Normalize(root), version);
        }
        return null;
    }

    /// <summary>The kcap app a copy running from a legacy root at <paramref name="ownRoot"/> hands its launch to, or
    /// null: that install owns the machine, and it retires this one when it runs.</summary>
    public static string? Successor(string ownRoot, string? localAppData) {
        if (!IsLegacy(ownRoot)) return null;
        return Candidates(ownRoot, localAppData, KcapFolderName)
            .Where(root => !LegacyPaths.Same(root, ownRoot))
            .Select(VelopackRoot.AppIn)
            .FirstOrDefault(app => app is not null
                && string.Equals(Path.GetFileName(app), VelopackRoot.AppExecutable, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLegacy(string root) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), FolderName, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Candidates(string ownRoot, string? localAppData, string folder) {
        if (!string.IsNullOrEmpty(localAppData)) yield return Path.Combine(localAppData, folder);
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownRoot))) is { } parent)
            yield return Path.Combine(parent, folder);
    }
}
