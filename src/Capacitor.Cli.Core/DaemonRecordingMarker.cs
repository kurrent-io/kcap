using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>&lt;name&gt;.recording</c> marker: written at boot by a daemon with recording on, removed at boot by one with it
/// off and at a clean exit. A terminal hook reads it before it opens the daemon's socket, so with recording off a hook
/// does no socket I/O at all. A marker a crash left behind costs a hook one bounded exchange, and the hello still decides
/// whether the hand-over frame is sent.
/// </summary>
public static class DaemonRecordingMarker {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes the marker when <paramref name="recording"/> is on and removes it otherwise. Never throws: a hook
    /// that finds no marker only starts the watcher it always did.</summary>
    public static void Sync(DaemonStore store, string daemonName, bool recording) {
        if (!recording) {
            Delete(store, daemonName);

            return;
        }

        try {
            store.EnsureDirectory();
            AtomicFile.Replace(store.RecordingMarkerPath(daemonName), "recording", OwnerOnly, followLink: false, durable: false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A daemon that can't write it is never handed a terminal session, which then gets a watcher.
        }
    }

    public static bool Exists(DaemonStore store, string daemonName) {
        try {
            return File.Exists(store.RecordingMarkerPath(daemonName));
        } catch (ArgumentException) {
            return false;
        }
    }

    public static void Delete(DaemonStore store, string daemonName) {
        try { File.Delete(store.RecordingMarkerPath(daemonName)); } catch { /* best-effort */ }
    }
}
