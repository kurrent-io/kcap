using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="AuthProviderDiscovery"/>'s in-process memo. It sits in front of
/// <see cref="AuthProviderCache"/>'s on-disk store, so if it answers for the wrong server the
/// correctly-keyed disk lookup never gets a chance to.
/// </summary>
public class AuthProviderDiscoveryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _a = WireMockServer.Start();
    readonly WireMockServer _b = WireMockServer.Start();

    // One per test, which is the whole isolation story: the memo lives here rather than on a static,
    // so a value cached by one test cannot be observed by another.
    readonly AuthProviderDiscovery _discovery = new(new PlainHttpClientFactory(), TimeProvider.System);

    public void Dispose() {
        _a.Stop();
        _b.Stop();
    }

    static void StubProvider(WireMockServer server, string provider) =>
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"provider\":\"{provider}\"}}"));

    Task<string> Discover(WireMockServer server) =>
        _discovery.DiscoverAsync(
                server.Urls[0], Config.Root, Resolutions.At(server.Urls[0], Config.Root),
                AuthFixtures.NewTokenStore(Config.Root));

    /// <summary>
    /// Two servers in one process must each get their own answer. `kcap setup` reaches this whenever
    /// auth returns a Retarget: it resolves one server, then resolves the tenant it was pointed at.
    /// </summary>
    [Test]
    public async Task Discovery_is_memoized_per_base_url() {
        StubProvider(_a, "WorkOS");
        StubProvider(_b, "GitHub");

        await Assert.That(await Discover(_a)).IsEqualTo("WorkOS");
        await Assert.That(await Discover(_b)).IsEqualTo("GitHub")
            .Because("a memo shared across base URLs hands the first server's provider to the second");
    }

    /// <summary>The proxy a server names is the one `kcap machine create` sends the operator's token to;
    /// absent from an older server, it is no answer, and a redirected answer is not believed.</summary>
    [Test]
    [Arguments("named", "https://proxy.example")]
    [Arguments("absent", null)]
    [Arguments("redirected", null)]
    public async Task The_server_names_its_auth_proxy_only_in_an_unredirected_answer(string answer, string? expected) {
        var body = answer == "absent" ? """{"provider":"WorkOS"}""" : """{"provider":"WorkOS","auth_proxy_url":"https://proxy.example"}""";

        if (answer == "redirected") {
            _a.Given(Request.Create().WithPath("/auth/config").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{_b.Urls[0]}/auth/config"));
            _b.Given(Request.Create().WithPath("/auth/config").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        } else {
            _a.Given(Request.Create().WithPath("/auth/config").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        }

        await Assert.That(await _discovery.AuthProxyUrlAsync(_a.Urls[0], Config.Root)).IsEqualTo(expected);
    }

    /// <summary>The memo must still spare a repeat call its round trip.</summary>
    [Test]
    public async Task A_repeat_discovery_against_one_server_does_not_probe_twice() {
        StubProvider(_a, "WorkOS");

        await Discover(_a);
        await Discover(_a);

        await Assert.That(_a.LogEntries.Count).IsEqualTo(1)
            .Because("the memo exists to skip the /auth/config round trip");
    }
}
