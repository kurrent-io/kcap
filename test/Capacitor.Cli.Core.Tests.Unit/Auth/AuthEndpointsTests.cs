using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class AuthEndpointsTests {
    [Test]
    public async Task An_unset_override_leaves_the_fleet_default() {
        var endpoints = new AuthEndpoints(ProxyOverride: null, SignupOverride: null);

        await Assert.That(endpoints.ProxyUrl).IsEqualTo(AuthEndpoints.DefaultProxyUrl);
        await Assert.That(endpoints.SignupUrl).IsEqualTo(AuthEndpoints.DefaultSignupUrl);
    }

    [Test]
    public async Task An_override_wins_and_loses_its_trailing_slash() {
        var endpoints = new AuthEndpoints("https://local-proxy.test/", "https://local-signup.test/");

        await Assert.That(endpoints.ProxyUrl).IsEqualTo("https://local-proxy.test");
        await Assert.That(endpoints.SignupUrl).IsEqualTo("https://local-signup.test");
    }

    /// <summary>
    /// The variable names are the contract, and nothing else in the tree reads either one: a typo or
    /// a swap here resolves one endpoint to the other's host with every other test still green.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_each_endpoints_own_variable() {
        // Literals, not the constants: setting through the constants stays self-consistent when the
        // two names are swapped, which is the mistake this exists to catch.
        using var proxy  = EnvScope.Exclusive("KCAP_AUTH_PROXY_URL", "https://env-proxy.test");
        using var signup = EnvScope.Exclusive("KCAP_SIGNUP_URL",     "https://env-signup.test");

        var endpoints = AuthEndpoints.FromEnvironment();

        await Assert.That(endpoints.ProxyUrl).IsEqualTo("https://env-proxy.test");
        await Assert.That(endpoints.SignupUrl).IsEqualTo("https://env-signup.test");
    }

    /// <summary>Each endpoint reads its own variable: one override must not move the other.</summary>
    [Test]
    public async Task The_two_overrides_are_independent() {
        var endpoints = new AuthEndpoints("https://local-proxy.test", SignupOverride: null);

        await Assert.That(endpoints.ProxyUrl).IsEqualTo("https://local-proxy.test");
        await Assert.That(endpoints.SignupUrl).IsEqualTo(AuthEndpoints.DefaultSignupUrl);
    }

    /// <summary>A call made for one server takes the override, then the proxy that server names, then
    /// Kurrent's.</summary>
    [Test]
    [Arguments(null, null, AuthEndpoints.DefaultProxyUrl)]
    [Arguments(null, "  ", AuthEndpoints.DefaultProxyUrl)]
    [Arguments(null, "https://proxy.example/", "https://proxy.example")]
    [Arguments("http://127.0.0.1:9", "https://proxy.example", "http://127.0.0.1:9")]
    public async Task A_server_scoped_call_prefers_the_override_then_the_server(string? overridden, string? server, string expected) =>
        await Assert.That(new AuthEndpoints(overridden, null).ProxyUrlFor(server)).IsEqualTo(expected);
}
