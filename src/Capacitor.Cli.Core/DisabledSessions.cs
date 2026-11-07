namespace Capacitor.Cli.Core;

/// <summary>
/// Disk-backed marker store recording sessions that the user disabled via
/// <c>kcap disable</c>. Every vendor's hook checks <see cref="IsDisabled"/> before doing any
/// server-bound work, and the daemon before it records a source, so a disabled session never
/// re-sends transcript data after its server-side record was deleted.
/// </summary>
public static class DisabledSessions {
    static string GetDisabledDir(ConfigRoot config) => config.Path("disabled");

    public static bool IsDisabled(string sessionId, ConfigRoot config) =>
        File.Exists(Path.Combine(GetDisabledDir(config), sessionId));

    public static void Mark(string sessionId, ConfigRoot config) {
        var dir = GetDisabledDir(config);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sessionId), "");
    }

    public static void RemoveMarker(string sessionId, ConfigRoot config) {
        var path = Path.Combine(GetDisabledDir(config), sessionId);

        try { File.Delete(path); } catch {
            /* ignore */
        }
    }
}
