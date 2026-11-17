using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Marker + detection helpers for <c>~/.copilot/hooks/kcap.json</c>. Mirror of
/// <see cref="CursorHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class CopilotHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Copilot hooks via setup or
    /// <c>kcap plugin install --copilot</c>. Marker file presence OR an
    /// existing <c>kcap hook --copilot</c> entry in kcap.json — the
    /// hooks-json fallback covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string hooksPath) =>
        HooksJson.IsInstalled(hooksPath, MarkerFileName, CopilotHooksParser.EntryReferencesCapacitorCopilotHook);

    public static string? ReadMarker(string hooksPath) => InstallMarker.Read(hooksPath, MarkerFileName);

    public static void WriteMarker(string hooksPath) => InstallMarker.Write(hooksPath, MarkerFileName);

    public static void DeleteMarker(string hooksPath) => InstallMarker.Delete(hooksPath, MarkerFileName);
}
