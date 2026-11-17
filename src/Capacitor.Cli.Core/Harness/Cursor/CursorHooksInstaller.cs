using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>
/// Marker + detection helpers for <c>~/.cursor/hooks.json</c>. Mirror of
/// <see cref="CodexHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class CursorHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Cursor hooks via setup or
    /// <c>kcap plugin install --cursor</c>. Marker file presence OR an
    /// existing <c>kcap hook --cursor</c> entry in hooks.json — the
    /// hooks-json fallback covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string hooksPath) =>
        HooksJson.IsInstalled(hooksPath, MarkerFileName, CursorHooksParser.EntryReferencesCapacitorCursorHook);

    public static string? ReadMarker(string hooksPath) => InstallMarker.Read(hooksPath, MarkerFileName);

    public static void WriteMarker(string hooksPath) => InstallMarker.Write(hooksPath, MarkerFileName);

    public static void DeleteMarker(string hooksPath) => InstallMarker.Delete(hooksPath, MarkerFileName);
}
