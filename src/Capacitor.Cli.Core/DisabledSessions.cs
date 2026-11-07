namespace Capacitor.Cli.Core;

/// <summary>
/// Disk-backed markers for sessions kcap keeps quiet. Every vendor's hook checks <see cref="IsDisabled"/> before any
/// server-bound work, and that covers two kinds that are otherwise nothing alike.
///
/// <para>A person's, from <c>kcap disable</c>: their record was deleted, so what is spooled is discarded, the daemon
/// refuses the session, and nothing removes the marker. A session's end can't lift it, because a daemon or a person can
/// resume that session under the same id. <see cref="IsDisabledByPerson"/> reads this kind alone.</para>
///
/// <para>A hook's exclusion, for a repository out of the profile's scope or a start the server refused: the hooks go
/// quiet for this run and nothing else changes. It comes off at the session's end, so a resume is judged afresh.</para>
///
/// <para>The two sit in separate directories so no exclusion can touch a person's marker. An older binary wrote both
/// kinds to the person's directory, and reading such a marker as a person's errs towards keeping a session quiet.</para>
/// </summary>
public static class DisabledSessions {
    const string Unconfirmed = "unconfirmed";

    static string GetDisabledDir(ConfigRoot config) => config.Path("disabled");
    static string GetExcludedDir(ConfigRoot config) => config.Path("excluded");

    /// <summary>Whether this session's hooks stay quiet, for either reason.</summary>
    public static bool IsDisabled(string sessionId, ConfigRoot config) =>
        IsDisabledByPerson(sessionId, config) || File.Exists(Path.Combine(GetExcludedDir(config), sessionId));

    /// <summary>Whether a person disabled the session. <c>kcap disable</c> marks the canonical id, while a spool or a
    /// hook keeps the vendor's spelling, so either finds it.</summary>
    public static bool IsDisabledByPerson(string sessionId, ConfigRoot config) =>
        File.Exists(Path.Combine(GetDisabledDir(config), sessionId))
     || SessionIds.Canonical(sessionId) is { } canonical && canonical != sessionId && File.Exists(Path.Combine(GetDisabledDir(config), canonical));

    /// <summary>A person's disable. A marker already there keeps what it holds: the stop that follows can be cut short,
    /// and an emptied marker would read as clean to the next run.</summary>
    public static void Mark(string sessionId, ConfigRoot config) {
        var dir  = GetDisabledDir(config);
        var path = Path.Combine(dir, sessionId);
        if (File.Exists(path)) return;

        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "");
    }

    /// <summary>A hook's exclusion of this run, lifted by <see cref="RemoveExclusion"/>.</summary>
    public static void Exclude(string sessionId, ConfigRoot config) {
        var dir = GetExcludedDir(config);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sessionId), "");
    }

    public static void RemoveExclusion(string sessionId, ConfigRoot config) {
        try { File.Delete(Path.Combine(GetExcludedDir(config), sessionId)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind, the session stays quiet, and the next end tries again.
        }
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
