namespace Capacitor.Cli.Core;

public static class PermissionRequestHook {
    /// <summary>How long kcap-server holds a permission request before answering it with a deny, counted from when
    /// the daemon registers it.</summary>
    public static readonly TimeSpan ServerExpiry = TimeSpan.FromHours(10);

    /// <summary>What a vendor gives a PermissionRequest hook that waits for the server's answer. A vendor that gives up
    /// first drops that answer and leaves its own prompt open. Its clock starts before the server's and never runs ahead
    /// of it, so the margin is for registration, which waits out a lost connection: a request raised while the daemon is
    /// offline still gets its answer if the daemon reconnects within the margin.</summary>
    public static readonly TimeSpan Timeout = ServerExpiry + TimeSpan.FromHours(14);
}
