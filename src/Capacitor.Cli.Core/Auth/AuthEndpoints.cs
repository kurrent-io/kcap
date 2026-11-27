namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Where the fleet-wide auth services live, resolved once at the composition root.
///
/// <para>Both take an internal override so a dev build can be pointed at a local stand-in. Before a
/// server is chosen they are Kurrent's; a call made for one server takes the proxy that server's
/// <c>/auth/config</c> names, through <see cref="ProxyUrlFor"/>. A value rather than a reader, so the
/// lane that resolves an endpoint and the command that reports it to the operator cannot answer
/// differently.</para>
/// </summary>
public sealed record AuthEndpoints(string? ProxyOverride, string? SignupOverride) {
    public const string ProxyUrlVar  = "KCAP_AUTH_PROXY_URL";
    public const string SignupUrlVar = "KCAP_SIGNUP_URL";

    public const string DefaultProxyUrl  = "https://auth.kcap.ai";
    public const string DefaultSignupUrl = "https://capacitor.kurrent.io";

    /// <summary>Neither endpoint overridden.</summary>
    public static readonly AuthEndpoints Defaults = new(null, null);

    /// <summary>This process's endpoints. Call once, in <c>Main</c> or the composition root.</summary>
    public static AuthEndpoints FromEnvironment() => new(
        Environment.GetEnvironmentVariable(ProxyUrlVar),
        Environment.GetEnvironmentVariable(SignupUrlVar));

    /// <summary><c>KCAP_AUTH_PROXY_URL</c> is an internal dev/test override; not documented for end
    /// users.</summary>
    public string ProxyUrl => Resolve(ProxyOverride, DefaultProxyUrl);

    /// <summary>The override, then the proxy the server names, then <see cref="DefaultProxyUrl"/>. For a
    /// call made on behalf of one server; discovery before a server is chosen uses <see cref="ProxyUrl"/>.</summary>
    public string ProxyUrlFor(string? serverProxyUrl) =>
        Resolve(ProxyOverride ?? (string.IsNullOrWhiteSpace(serverProxyUrl) ? null : serverProxyUrl.Trim()), DefaultProxyUrl);

    /// <summary><see cref="ProxyUrlFor"/>, refusing a server-named proxy the operator's bearer could
    /// leak to: it must be https, or http on loopback. Refused rather than replaced by the default, so
    /// the token never goes to a proxy the server did not name.</summary>
    public string? TryResolveProxyUrlFor(string? serverProxyUrl, out string? problem) {
        problem = null;

        if (ProxyOverride is not null || string.IsNullOrWhiteSpace(serverProxyUrl)) return ProxyUrlFor(serverProxyUrl);

        var named = serverProxyUrl.Trim();

        if (Uri.TryCreate(named, UriKind.Absolute, out var uri)
         && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return ProxyUrlFor(named);

        problem = $"The server's auth_proxy_url must be https (or http on loopback) — refusing to send your sign-in to {named}.";

        return null;
    }

    /// <summary><c>KCAP_SIGNUP_URL</c> is an internal dev/preview override; not documented for end
    /// users.</summary>
    public string SignupUrl => Resolve(SignupOverride, DefaultSignupUrl);

    static string Resolve(string? over, string fallback) => (over ?? fallback).TrimEnd('/');
}
