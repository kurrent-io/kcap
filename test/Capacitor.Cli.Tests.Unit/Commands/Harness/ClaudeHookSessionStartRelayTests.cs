using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The notice a daemon-hosted Claude session sends its daemon when SessionStart fires, which is what
/// a launch prompt held behind a startup screen is waiting for. On a clock that never moves, so the
/// relay's cap cannot run out under a loaded host.
public class ClaudeHookSessionStartRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";
    const string Route = "/tok/claude/session-start";

    /// Answers every server post with 200 so the hook reaches its normal end.
    sealed class OkHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    /// The relay rides ahead of client creation, so it is exercised through HandleWithDeps and
    /// asserted on the bridge itself, never on the server.
    async Task<int> RunAsync(HostedAgent hosted, string eventName) {
        using var client = new HttpClient(new OkHandler());
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"{{{Sid}}}","cwd":"/tmp","source":"startup"}""";
        return await new ClaudeHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(new FakeTimeProvider()), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), SystemProcessStarter.Instance, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .HandleWithDeps(new HookSpool(Config.Root, time: TimeProvider.System, server: null), new StringReader(payload), () => Task.FromResult(new AuthAttempt(client, AuthStatus.Ok, null, null)), new StringWriter());
    }

    static WireMockServer Bridge() {
        var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath(Route).UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        return bridge;
    }

    [Test]
    public async Task A_hosted_session_start_reaches_the_daemon_bridge() {
        using var bridge = Bridge();
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(hosted, "SessionStart");

        await Assert.That(exit).IsEqualTo(0);
        var body = JsonNode.Parse(bridge.LogEntries.Single(e => e.RequestMessage.Path == Route).RequestMessage.Body!)!;
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["cwd"]!.GetValue<string>()).IsEqualTo("/tmp");
    }

    /// A paste released by anything but SessionStart could land on a screen ahead of the REPL.
    [Test]
    public async Task Other_hook_events_do_not_report_a_session_start() {
        using var bridge = Bridge();
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        await RunAsync(hosted, "Notification");

        await Assert.That(bridge.LogEntries.Count(e => e.RequestMessage.Path == Route)).IsEqualTo(0);
    }

    /// A session the user runs themselves has a daemon URL only by accident of environment
    /// inheritance; without the agent id nothing identifies it to a daemon.
    [Test]
    public async Task An_unhosted_session_start_relays_nothing() {
        using var bridge = Bridge();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        await RunAsync(unhosted, "SessionStart");

        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
