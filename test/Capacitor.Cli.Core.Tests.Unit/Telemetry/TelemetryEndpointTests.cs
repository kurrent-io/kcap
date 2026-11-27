using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// A profile's <c>hosts.telemetry</c> decides where a run's events go, and each endpoint keeps its own
/// spool: events parked under one profile's host must never be replayed to another's.
/// </summary>
public class TelemetryEndpointTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _host = WireMockServer.Start();

    public void Dispose() => _host.Stop();

    static TelemetryEvent Event(string name) => new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UtcNow);

    static TelemetryStartup Startup(string endpoint) =>
        new("status", null, "https://signup.example", Suppressed: false, Debug: false) { Endpoint = endpoint };

    [Test]
    public async Task Events_go_to_the_runs_endpoint_and_not_another_endpoints_spool() {
        _host.Given(Request.Create().WithPath("/batch/").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        TelemetrySpool.For(Config.Root).Append([Event("parked_for_the_default_host")]);

        var sink = CliTelemetry.LiveSink(Startup(_host.Urls[0]), Config.Root, TimeProvider.System);
        sink.Enqueue(Event("sent_here"));

        await Assert.That(await sink.FlushAsync("device-1", null, TimeSpan.FromSeconds(5))).IsTrue();

        var body = _host.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("sent_here");
        await Assert.That(body).DoesNotContain("parked_for_the_default_host");
        await Assert.That(TelemetrySpool.For(Config.Root).DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("https://phog.kurrent.io", "https://phog.kurrent.io/", true)]
    [Arguments("https://phog.kurrent.io", "https://PHOG.kurrent.io", true)]
    [Arguments("https://ph.example", "https://phog.kurrent.io", false)]
    [Arguments("https://ph.example", "https://other.example", false)]
    public async Task One_spool_per_endpoint(string first, string second, bool shared) {
        TelemetrySpool.For(Config.Root, first).Append([Event("parked")]);

        await Assert.That(TelemetrySpool.For(Config.Root, second).DrainAll().Count).IsEqualTo(shared ? 1 : 0);
    }

    /// <summary>An older CLI spooled under the unsuffixed name for the one host it knew.</summary>
    [Test]
    public async Task The_default_host_keeps_the_spool_an_older_cli_wrote() {
        var legacy = Config.Root.Path("telemetry-spool.jsonl");
        new TelemetrySpool(legacy).Append([Event("from_an_older_cli")]);

        await Assert.That(TelemetrySpool.For(Config.Root, HostsSettings.DefaultTelemetry).DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Opting_out_clears_every_endpoints_spool() {
        TelemetrySpool.For(Config.Root).Append([Event("a")]);
        TelemetrySpool.For(Config.Root, "https://ph.example").Append([Event("b")]);
        TelemetrySpool.For(Config.Root, "https://ph.example").MarkUnreachable(DateTimeOffset.UtcNow);

        TelemetryState.SetEnabled(false, Config.Root);

        await Assert.That(TelemetrySpool.For(Config.Root).DrainAll().Count).IsEqualTo(0);
        await Assert.That(TelemetrySpool.For(Config.Root, "https://ph.example").DrainAll().Count).IsEqualTo(0);
        await Assert.That(TelemetrySpool.For(Config.Root, "https://ph.example").UnreachableSince() is null).IsTrue();
    }
}
