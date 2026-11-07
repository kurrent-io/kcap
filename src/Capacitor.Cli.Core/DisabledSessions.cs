namespace Capacitor.Cli.Core;

/// <summary>
/// Disk-backed marker store recording sessions that the user disabled via
/// <c>kcap disable</c>. Every vendor's hook checks <see cref="IsDisabled"/> before doing any
/// server-bound work, and the daemon before it records a source, so a disabled session never
/// re-sends transcript data after its server-side record was deleted. Nothing removes a marker: the server's deletion is
/// permanent, and a session's end can't lift it, because a daemon or a person can resume that session under the same id.
/// </summary>
public static class DisabledSessions {
    const string Unconfirmed = "unconfirmed";

    static string GetDisabledDir(ConfigRoot config) => config.Path("disabled");

    public static bool IsDisabled(string sessionId, ConfigRoot config) =>
        File.Exists(Path.Combine(GetDisabledDir(config), sessionId));

    public static void Mark(string sessionId, ConfigRoot config) {
        var dir = GetDisabledDir(config);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sessionId), "");
    }

    /// <summary>Records that a disable couldn't say recording stopped. Its claims are gone by then, so a later run has
    /// nobody to ask, and without this it would read as clean with nothing behind it.</summary>
    public static void MarkUnconfirmed(string sessionId, ConfigRoot config) =>
        File.WriteAllText(Path.Combine(GetDisabledDir(config), sessionId), Unconfirmed);

    public static bool IsUnconfirmed(string sessionId, ConfigRoot config) {
        try {
            return File.ReadAllText(Path.Combine(GetDisabledDir(config), sessionId)) == Unconfirmed;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }
}
