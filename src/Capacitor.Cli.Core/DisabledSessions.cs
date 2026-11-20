using System.Text;
using Capacitor.Cli.Core.Auth;

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
///
/// <para>A marker is named by the session id itself, so an id that is not a bare file name has none: it reads as no
/// marker, and writing or lifting one does nothing. A Pi agent's resume token, its session file's absolute path, is
/// such an id.</para>
/// </summary>
public static class DisabledSessions {
    static string GetDisabledDir(ConfigRoot config) => config.Path("disabled");
    static string GetExcludedDir(ConfigRoot config) => config.Path("excluded");

    // Path.Combine drops the directory for a rooted id, and a relative path can climb out of it.
    static string? Marker(string dir, string sessionId) =>
        sessionId is "" or "." or ".." || Path.GetFileName(sessionId) != sessionId ? null : Path.Combine(dir, sessionId);

    static bool HasMarker(string dir, string sessionId) => Marker(dir, sessionId) is { } path && File.Exists(path);

    /// <summary>Whether this session's hooks stay quiet, for either reason.</summary>
    public static bool IsDisabled(string sessionId, ConfigRoot config) =>
        IsDisabledByPerson(sessionId, config) || HasMarker(GetExcludedDir(config), sessionId);

    /// <summary>Whether a person disabled the session. <c>kcap disable</c> marks the canonical id, while a spool or a
    /// hook keeps the vendor's spelling, so either finds it.</summary>
    public static bool IsDisabledByPerson(string sessionId, ConfigRoot config) =>
        HasMarker(GetDisabledDir(config), sessionId)
     || SessionIds.Canonical(sessionId) is { } canonical && canonical != sessionId && HasMarker(GetDisabledDir(config), canonical);

    /// <summary>A person's disable. A marker already there keeps what it holds: the stop that follows can be cut short,
    /// and an emptied marker would read as clean to the next run. Created only if absent, because another run can write
    /// one at any moment.</summary>
    public static void Mark(string sessionId, ConfigRoot config) {
        var dir = GetDisabledDir(config);
        if (Marker(dir, sessionId) is not { } path || File.Exists(path)) return;

        Directory.CreateDirectory(dir);

        try {
            // Shared, or on Windows a run recording its deletion beside this one is refused while this is open.
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
        } catch (IOException) when (File.Exists(path)) {
            // Another run's marker, which stays as it is.
        }
    }

    /// <summary>A hook's exclusion of this run, lifted by <see cref="RemoveExclusion"/>.</summary>
    public static void Exclude(string sessionId, ConfigRoot config) {
        var dir = GetExcludedDir(config);
        if (Marker(dir, sessionId) is not { } path) return;

        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "");
    }

    public static void RemoveExclusion(string sessionId, ConfigRoot config) {
        if (Marker(GetExcludedDir(config), sessionId) is not { } path) return;

        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind, the session stays quiet, and the next end tries again.
        }
    }

    const string DeletedUnconfirmed = "deleted-unconfirmed";

    /// <summary>Records that a deletion ran while recording wasn't confirmed stopped. Nothing clears it: whatever landed
    /// behind that deletion stays out of reach from this machine whatever a later run finds.</summary>
    public static void MarkDeletedUnconfirmed(string sessionId, ConfigRoot config) {
        var dir = GetDisabledDir(config);
        if (Marker(dir, sessionId) is not { } path) return;

        Directory.CreateDirectory(dir);

        using var marker = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        marker.Write(Encoding.UTF8.GetBytes(DeletedUnconfirmed));
    }

    public static bool WasDeletedUnconfirmed(string sessionId, ConfigRoot config) {
        if (Marker(GetDisabledDir(config), sessionId) is not { } path) return false;

        try {
            return File.ReadAllTextShared(path) == DeletedUnconfirmed;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    // Beside the marker rather than in it, so neither fact's write can overwrite the other.
    static string? OtherServersPath(string sessionId, ConfigRoot config) =>
        Marker(GetDisabledDir(config), sessionId) is { } marker ? $"{marker}.other-server" : null;

    /// <summary>Records a server a daemon sent this session to, which a deletion from this profile never reaches. Nothing
    /// clears it: nothing here can tell when that server's copy goes, and the daemon holds no claim to say so next time.</summary>
    public static void MarkOnOtherServer(string sessionId, string server, ConfigRoot config) {
        if (OtherServersPath(sessionId, config) is not { } path || OtherServers(sessionId, config).Any(known => SameServer(known, server))) return;

        Directory.CreateDirectory(GetDisabledDir(config));
        File.AppendAllText(path, server + "\n");
    }

    public static IReadOnlyList<string> OtherServers(string sessionId, ConfigRoot config) {
        if (OtherServersPath(sessionId, config) is not { } path) return [];

        try {
            return File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    public static bool SameServer(string a, string b) => (ServerIdentity.Canonicalize(a) ?? a) == (ServerIdentity.Canonicalize(b) ?? b);
}
