using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Whether a server can stop a source. Nothing signals that a server pushes <c>StopSource</c>, and on a server
/// that doesn't, a session's end would wait out its drain and leave the daemon recording.</summary>
internal static class RecordingServerFloor {
    /// <summary>The first server version that pushes <c>StopSource</c> and answers <c>WatcherDetach</c>.</summary>
    public const string MinimumVersion = "1.5.0";

    /// <summary>A version nobody has seen, or can't be read, is below the floor.</summary>
    public static bool Met(string? serverVersion) => PrereleaseSemver.Compare(serverVersion, MinimumVersion) >= 0;
}
