namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The timestamp the kcap plugin writes beside a session's transcript on every session event, when an OpenCode v2
/// server shared across sessions runs it. Its presence is the plugin saying the session has no end signal, since
/// that server outlives every session in it. The plugin writes it in <see cref="WatcherHeartbeat.Touch"/>'s format,
/// temp-then-rename, at the path <c>OpenCodeExtensionInstaller.ExtensionContent</c> derives the same way.
/// </summary>
public static class OpenCodeSessionHeartbeat {
    public static string PathFor(string transcriptPath) => Path.ChangeExtension(transcriptPath, ".heartbeat");

    /// <summary>Removes the heartbeat when the session ends idle. The plugin starts a session whose heartbeat is gone
    /// again on its next event, which is how a resumed conversation comes back.</summary>
    public static void Delete(string transcriptPath) {
        try {
            File.Delete(PathFor(transcriptPath));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The session still ends; the plugin then only misses the cue to start it again.
        }
    }
}
