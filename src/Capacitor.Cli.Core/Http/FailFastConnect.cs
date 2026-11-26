namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The handler for a host a command calls on the side — telemetry, the update check — whose failure
/// must cost the command next to nothing. A firewall that drops packets leaves a connect hanging until
/// the request's own timeout, so the connection, DNS and TLS included, gets a bound of its own.
/// </summary>
public static class FailFastConnect {
    /// <summary>Connect plus TLS to the default Cloudflare-fronted hosts measures 80–155 ms; a second
    /// leaves room for a slow link or a farther self-hosted endpoint.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static SocketsHttpHandler Handler() => new() { ConnectTimeout = Timeout };
}
