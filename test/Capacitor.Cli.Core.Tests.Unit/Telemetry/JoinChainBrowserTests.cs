using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// A real browser must execute location.replace and send SameSite=Lax cookies across the
/// 127.0.0.1-to-localhost navigation; HttpClient cannot verify either behavior.
/// KCAP_JOIN_BROWSER_E2E and KCAP_JOIN_BROWSER_HANDSHAKE opt in: the driver publishes a URL and
/// waits for an external browser to visit it. The cookie carries $device_id without distinct_id
/// so identity merging is exercised without emitting an analytics event.
/// </summary>
public class JoinChainBrowserTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string GateEnvVar      = "KCAP_JOIN_BROWSER_E2E";
    const string HandshakeEnvVar = "KCAP_JOIN_BROWSER_HANDSHAKE";

    /// <summary>Stands in for the per-login CSRF state OidcClient puts in the authorize URL. The
    /// closing page's redirect is gated on the callback echoing it, so the driver must send it back.
    /// </summary>
    const string State = "browser-e2e-state";

    /// <summary>Generous: a human or an agent has to drive a browser between the handshake being
    /// written and the return hop landing.</summary>
    static readonly TimeSpan DriverBudget = TimeSpan.FromMinutes(3);

    [Test]
    public async Task A_real_browser_carries_the_key_out_and_the_web_identity_back() {
        var baseUrl   = Environment.GetEnvironmentVariable(GateEnvVar);
        var handshake = Environment.GetEnvironmentVariable(HandshakeEnvVar);

        Skip.Unless(!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(handshake),
            $"Set {GateEnvVar}=<base url> and {HandshakeEnvVar}=<path> to run the browser chain.");

        try {
            var probe = TelemetryProbe.Live("setup", new ConfigRoot(Tmp.Path), signupUrl: new AuthEndpoints(null, baseUrl).SignupUrl);
            var key   = probe.Join.Mint();
            await Assert.That(key).IsNotNull();

            var port     = OAuthLoginFlow.GetAvailablePort();
            var redirect = $"http://127.0.0.1:{port}/callback";

            using var browser = new LoopbackBrowser(new RecordingBrowser(), TimeProvider.System, join: probe.Join) {
                DrainCap = DriverBudget, DisposeWait = TimeSpan.FromSeconds(10),
            };

            var invoke = browser.InvokeAsync(
                new BrowserOptions($"http://example.test/authorize?state={State}", redirect));

            // Published only after InvokeAsync has bound the listener, so a driver that navigates the
            // instant it sees this file cannot beat the port into existence.
            PublishHandshake(handshake!, baseUrl!, port, $"{redirect}?code=browser-e2e&state={State}");

            var result = await invoke;
            await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success)
                .Because("the browser's callback must complete sign-in, not fail it");

            // The hops and the return to /joined happen in the browser after the closing page is
            // served, so the merge lands asynchronously. Poll the shared properties for it.
            var merged = await WaitForMerge(probe, DriverBudget);

            await Assert.That(merged).IsNotNull()
                .Because("a real browser must carry the web identity back to /joined");
            await Assert.That(merged!["join_id"]!.GetValue<string>()).IsEqualTo(key);
            await Assert.That(merged["web_device_id_capacitor"]!.GetValue<string>()).IsNotEmpty()
                .Because("SameSite=Lax cookies must travel on this top-level navigation — the whole "
                       + "reason the closing page navigates instead of firing a beacon");
        } finally {
            try { File.Delete(handshake!); } catch { /* best effort */ }
        }
    }

    /// <summary>Everything the driver needs, written atomically so a poller never reads half a file.
    /// </summary>
    static void PublishHandshake(string path, string baseUrl, int port, string callbackUrl) {
        var payload = new JsonObject {
            ["callbackUrl"] = callbackUrl,
            ["joinedUrl"]   = $"http://127.0.0.1:{port}/joined",
            ["baseUrl"]     = baseUrl,
            ["port"]        = port,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var staging = path + ".tmp";
        File.WriteAllText(staging, payload);
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>
    /// The shared properties once the return hop has been accepted, or null if it never arrived.
    /// Probing with a real capture is how the other suites read shared state, and it is what a real
    /// event would carry.
    /// </summary>
    static async Task<JsonObject?> WaitForMerge(TelemetryProbe probe, TimeSpan budget) {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline) {
            probe.Sink.Discard();
            probe.Telemetry.Capture("cli_browser_e2e_probe", new JsonObject());

            if (probe.Events.Count > 0 && probe.Events[^1].Properties["web_device_id_capacitor"] is not null)
                return probe.Events[^1].Properties;

            await Task.Delay(500);
        }

        return null;
    }
}
