namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The handler for a host a command calls on the side — telemetry, the update check — whose failure
/// must cost the command next to nothing. A firewall that drops packets leaves a connect hanging until
/// the request's own timeout, so the connection, DNS and TLS included, gets a bound of its own.
/// </summary>
public static class FailFastConnect {
    /// <summary>Connect plus TLS to these Cloudflare-fronted hosts measures 80–155 ms, so this leaves
    /// about three times that before a host counts as unreachable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    public static SocketsHttpHandler Handler() => new() { ConnectTimeout = Timeout };
}
