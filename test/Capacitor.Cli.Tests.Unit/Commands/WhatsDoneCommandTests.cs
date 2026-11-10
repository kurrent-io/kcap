using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WhatsDoneCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempHome] public required TempHome Home { get; init; }

    sealed class NotFound : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    WhatsDoneCommand Command(ICapacitorHttpClient http) =>
        new(Config.Root, Resolutions.At("https://example.test", Config.Root), TestHarnesses.Under(Home), http, TimeProvider.System);

    /// <summary>A summary reads the session's recap off the server and writes it back.</summary>
    [Test]
    public async Task A_disabled_session_gets_no_summary() {
        using var handler = new NotFound();
        var       http    = new RecordingCapacitorHttpClient(handler);
        DisabledSessions.Mark(Sid, Config.Root);

        var exit = await Command(http).GenerateForSessionAsync("https://example.test", Sid, _ => { });

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEmpty();
    }

    /// <summary>A summary takes up to 90 seconds of model time, and this process runs on by itself.</summary>
    [Test]
    public async Task A_summary_finished_after_a_disable_is_not_posted() {
        var posts = 0;
        using var handler = new Counting(() => posts++);
        using var client  = new HttpClient(handler);
        DisabledSessions.Mark(Sid, Config.Root);

        var exit = await Command(new RecordingCapacitorHttpClient(handler)).PostSummaryAsync(client, "https://example.test", Sid, Summary, _ => { });

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(posts).IsEqualTo(0);
    }

    [Test]
    public async Task A_summary_of_a_session_nobody_disabled_is_posted() {
        var posts = 0;
        using var handler = new Counting(() => posts++);
        using var client  = new HttpClient(handler);

        var exit = await Command(new RecordingCapacitorHttpClient(handler)).PostSummaryAsync(client, "https://example.test", Sid, Summary, _ => { });

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(posts).IsEqualTo(1);
    }

    const string Sid = "0123456789abcdef0123456789abcdef";

    static readonly ClaudeCliResult Summary = new("What was done", "model", 1, 2, 3, 4, null, 1);

    sealed class Counting(Action onPost) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            onPost();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
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
                Config.Root, Resolutions.At("https://example.test", Config.Root), TestHarnesses.Under(Home), http, TimeProvider.System)
            .GenerateForSessionAsync("https://example.test", "session-1", _ => { });

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "ForBackgroundAsync" });
    }
}
