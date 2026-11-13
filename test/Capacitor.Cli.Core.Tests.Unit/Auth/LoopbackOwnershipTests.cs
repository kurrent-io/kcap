using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>A browser handed to the flow stays its caller's to dispose.</summary>
public class LoopbackOwnershipTests {
    // The code exchange rides the sign-in lane; against a stub, an unconfigured client is what
    // that lane resolves to.
    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());

    /// <summary>An externally supplied browser that would notice being disposed by the callee.</summary>
    sealed class DisposableFakeBrowser(string query) : IBrowser, IDisposable {
        public bool Disposed { get; private set; }

        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) =>
            Task.FromResult(new BrowserResult { ResultType = BrowserResultType.Success, Response = query });

        public void Dispose() => Disposed = true;
    }

    // Disposing an injected browser would tear down a test's stand-in mid-test — or, later, a
    // caller's shared instance. The state mismatch below makes the flow return null without any
    // network call; what matters is what happened to `fake`.
    [Test]
    public async Task An_injected_browser_is_never_disposed_by_the_callee() {
        var fake = new DisposableFakeBrowser("?code=abc&state=mismatch");

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            Github, "client-id", "http://127.0.0.1:1/exchange", new RecordingBrowser(), NoTelemetry.Join,
            TimeProvider.System,
            browser: fake, timeout: TimeSpan.FromSeconds(1));

        await Assert.That(token).IsNull();
        await Assert.That(fake.Disposed).IsFalse();
    }
}
