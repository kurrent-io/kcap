using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WhatsDoneCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempHome] public required TempHome Home { get; init; }

    sealed class NotFound : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Both entry points are unattended — one has already pointed stderr at a log file, the other runs
    /// inside a silent import loop, once per session. The interactive lane would write a re-auth hint
    /// nobody reads, multiplied by the session count.
    /// </summary>
    [Test]
    public async Task A_summary_run_draws_the_background_lane() {
        using var handler = new NotFound();
        var       http    = new RecordingCapacitorHttpClient(handler);

        var exit = await new WhatsDoneCommand(
                Config.Root, Resolutions.At("https://example.test", Config.Root), TestHarnesses.Under(Home), http, TimeProvider.System,
                LocalIntelligenceGate.For(Config.Root, NoLocalInstallation.Root))
            .GenerateForSessionAsync("https://example.test", "session-1", _ => { });

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "ForBackgroundAsync" });
    }

    /// <summary>A local installation with no background intelligence gets no summary: the run succeeds before it reads the
    /// recap, so no CLI is reached.</summary>
    [Test]
    public async Task A_local_installation_without_intelligence_is_skipped_before_any_request() {
        const string server = "http://localhost:41999";
        using var handler = new NotFound();
        var       http    = new RecordingCapacitorHttpClient(handler);
        var       log     = new List<string>();

        var exit = await new WhatsDoneCommand(
                Config.Root, Resolutions.At(server, Config.Root), TestHarnesses.Under(Home), http, TimeProvider.System,
                new LocalIntelligenceGate(Config.Root, () => Resolutions.InstalledAt(server)))
            .GenerateForSessionAsync(server, "session-1", log.Add);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEmpty();
        await Assert.That(log.Single()).Contains(LocalIntelligenceGate.NotSetUp);
    }
}
