namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Files the kcap plugin and the watcher share beside an OpenCode v2 session's transcript, at the paths
/// <c>OpenCodeExtensionInstaller.ExtensionContent</c> derives the same way.
/// <para>The heartbeat is the time of the session's last event, written by the plugin in
/// <see cref="WatcherHeartbeat.Touch"/>'s format when a server shared across sessions runs it. Its presence says the
/// session has no end signal, since that server outlives every session in it.</para>
/// <para>The end marker is the restart handshake. A watcher that ends the session on idle writes it; the plugin starts
/// the session again on each event while it exists, throttled; a recorder starting for the session removes it. A
/// restart that lands while the old watcher is still finishing, and so spawns nothing, is retried that way.</para>
/// </summary>
public static class OpenCodeSessionHeartbeat {
    public static string PathFor(string transcriptPath) => Path.ChangeExtension(transcriptPath, ".heartbeat");

    public static string EndedPathFor(string transcriptPath) => Path.ChangeExtension(transcriptPath, ".ended");

    /// <summary>Best effort: without the marker the session still ends, and starts again only when the plugin's
    /// process does.</summary>
    public static void MarkEnded(string transcriptPath, DateTimeOffset now) {
        try {
            WatcherHeartbeat.Touch(EndedPathFor(transcriptPath), now);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    public static void ClearEnded(string transcriptPath) {
        try {
            File.Delete(EndedPathFor(transcriptPath));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }
}
