namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>
/// Marker + detection helpers for kcap's hooks inside Gemini CLI's shared
/// <c>~/.gemini/settings.json</c>. Mirror of <c>CopilotHooksInstaller</c>, but
/// the detection fallback inspects the merged <c>hooks</c> block rather than a
/// kcap-owned file. The actual merge/unmerge lives in <c>PluginCommand</c>
/// (it must preserve user-authored hook entries — see <see cref="GeminiHooksParser"/>).
/// </summary>
public static class GeminiHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when kcap's Gemini hooks were previously installed. Marker file
    /// presence OR an existing <c>kcap hook --gemini</c> entry in settings.json
    /// (the latter covers pre-marker installs).
    /// </summary>
    public static bool IsInstalled(string settingsPath) =>
        HooksJson.IsInstalled(settingsPath, MarkerFileName, GeminiHooksParser.EntryReferencesCapacitorGeminiHook);

    public static string? ReadMarker(string settingsPath) => InstallMarker.Read(settingsPath, MarkerFileName);

    public static void WriteMarker(string settingsPath) => InstallMarker.Write(settingsPath, MarkerFileName);

    public static void DeleteMarker(string settingsPath) => InstallMarker.Delete(settingsPath, MarkerFileName);
}
