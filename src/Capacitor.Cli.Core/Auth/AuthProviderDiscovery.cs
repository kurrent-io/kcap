using System.Collections.Concurrent;
using System.Net.Http.Json;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Which auth provider a server announces at <c>/auth/config</c>, memoized for the container's life.
///
/// <para>Draws the anonymous lane per call: discovery must not spend or mint a credential, and the
/// caller may still be deciding whether to adopt the server it is asking about.</para>
/// </summary>
public sealed class AuthProviderDiscovery(IHttpClientFactory factory, TimeProvider time) {
    // Keyed by baseUrl, like the on-disk store: one process can discover against more than one
    // server — `kcap setup` retargeting to another tenant is the reachable case — and a single memo
    // would hand the first server's provider to the second, short-circuiting the on-disk lookup
    // before it can answer correctly.
    readonly ConcurrentDictionary<string, string> _memo = new(StringComparer.Ordinal);

    readonly ConcurrentDictionary<string, string?> _machineTokenUrls = new(StringComparer.Ordinal);

    public async Task<string> DiscoverAsync(
            string baseUrl, ConfigRoot config, ProfileContext profiles, TokenStore store,
            CancellationToken ct = default) {
        if (_memo.TryGetValue(baseUrl, out var memo)) {
            return memo;
        }

        // Reached with a URL nobody validated only from a command that resolved one itself; the
        // client verbs answer for their own callers before anything gets here. Without this the
        // relative-URL throw below lands in the catch and returns a provider, which reads as an
        // answer about a server that was never asked.
        if (!HttpClientExtensions.IsAcceptableUrl(baseUrl))
            throw new UnusableServerUrlException(HttpClientExtensions.SchemeMissingHint);

        // Cross-process cache: each hook invocation is a fresh process, so the memo above never helps
        // a hook. Skip the /auth/config round-trip when a recent result is on disk.
        var cached = AuthProviderCache.TryGet(baseUrl, config, time);

        if (cached is not null) {
            _memo[baseUrl] = cached;

            return cached;
        }

        if (await FetchAsync(baseUrl, config, ct) is { } provider) return provider;

        // Fallback: try existing tokens (don't cache — allow re-discovery next time)
        return (await store.LoadForProfileAsync(profiles.Name, ct))?.Provider ?? "None";
    }

    /// <summary>
    /// The server's <c>machine_token_url</c>, or null when it names none or cannot be asked — the
    /// caller then falls back to the default endpoint. A cached entry written without the field
    /// is not an answer, so it is asked again.
    ///
    /// <para>Only a server reached over https, or http on loopback, is believed: the mint sends the
    /// client secret to this URL, so a plaintext <c>/auth/config</c> anyone on the path could rewrite
    /// would hand them the credential.</para>
    /// </summary>
    public async Task<string?> MachineTokenUrlAsync(string baseUrl, ConfigRoot config, CancellationToken ct = default) {
        if (!MayNameTokenUrl(baseUrl)) return null;

        if (_machineTokenUrls.TryGetValue(baseUrl, out var memo)) return memo;

        if (AuthProviderCache.TryGetMachineTokenUrl(baseUrl, config, time, out var cached)) {
            _machineTokenUrls[baseUrl] = cached;

            return cached;
        }

        await FetchAsync(baseUrl, config, ct);

        return _machineTokenUrls.GetValueOrDefault(baseUrl);
    }

    /// <summary>
    /// The <c>machine_token_url</c> an <c>/auth/config</c> answer may record, or null. Every writer of
    /// the field goes through this: the mint sends the client secret there.
    ///
    /// <para>The anonymous lane follows redirects, and a plaintext hop anywhere in the chain could have
    /// rewritten the answer, so only an unredirected answer from a server reached safely names it.</para>
    /// </summary>
    public static string? TrustedTokenUrl(Uri asked, HttpResponseMessage response, string? named) =>
        response.RequestMessage?.RequestUri == asked && MayNameTokenUrl(asked.AbsoluteUri) ? named : null;

    static bool MayNameTokenUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
     && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    async Task<string?> FetchAsync(string baseUrl, ConfigRoot config, CancellationToken ct) {
        using var http = factory.CreateClient(CapacitorClients.Anonymous);

        try {
            var request  = new Uri($"{baseUrl}/auth/config");
            var response = await http.GetAsync(request, ct);

            if (response.IsSuccessStatusCode) {
                var discovered = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse, ct);
                var provider   = discovered?.Provider ?? "None";
                var tokenUrl   = TrustedTokenUrl(request, response, discovered?.MachineTokenUrl);
                _memo[baseUrl]             = provider;
                _machineTokenUrls[baseUrl] = tokenUrl;
                AuthProviderCache.Set(baseUrl, provider, config, time, tokenUrl); // only cache successful discovery

                return provider;
            }
        } catch {
            // Unreachable, refused or cancelled: the caller falls back rather than failing discovery.
            // Cancellation is not swallowed by this — the fallback takes the same token and surfaces
            // it once the answer is not already in hand.
        }

        return null;
    }
}
