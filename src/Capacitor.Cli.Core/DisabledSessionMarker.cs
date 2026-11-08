namespace Capacitor.Cli.Core;

/// <summary>Where <c>kcap disable</c> marks a session, for a process outside the CLI that must honour it too.</summary>
public static class DisabledSessionMarker {
    public static string PathFor(ConfigRoot config, string sessionId) => Path.Combine(config.Path("disabled"), sessionId);

    public static bool IsSet(ConfigRoot config, string sessionId) => File.Exists(PathFor(config, sessionId));
}
