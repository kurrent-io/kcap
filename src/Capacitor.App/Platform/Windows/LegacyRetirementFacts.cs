namespace Capacitor.App.Platform.Windows;

/// <summary>What a scan of the machine found about a legacy install, for <see cref="LegacyRetirementPlan"/>.</summary>
/// <param name="Processes">Processes running an app or updater image inside the legacy root.</param>
/// <param name="Shortcuts">Shortcut files that name the legacy root.</param>
/// <param name="Autostart">Values under the user's Run key that name the legacy root.</param>
/// <param name="LegacyCliOnPath">The legacy CLI whose folder is on the user PATH, or null.</param>
/// <param name="KeptPaths">Folders that must survive, such as the config directory and the daemon store.</param>
public sealed record LegacyRetirementFacts(
    LegacyWindowsInstall Legacy, string OwnRoot, string OwnCli,
    IReadOnlyList<RunningImage> Processes,
    IReadOnlyList<UninstallEntry> UninstallEntries,
    IReadOnlyList<string> Shortcuts,
    IReadOnlyList<string> Autostart,
    string? LegacyCliOnPath,
    IReadOnlyList<string> KeptPaths);
