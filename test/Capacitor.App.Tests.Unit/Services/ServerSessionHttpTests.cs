using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit.Services;

/// Builds the app's real authenticated HTTP lane against a WireMock server.
static class WireMockLane {
    public static async Task<(ICapacitorHttpClient Http, ProfileContext Profiles, ServiceProvider Provider)> BuildAsync(WireMockServer server, ConfigRoot root) {
        var profiles = Resolutions.At(server.Url!, root);
        await AuthFixtures.NewTokenStore(root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "alice",
            Provider = AuthProvider.GitHubApp, ServerUrl = server.Url!,
        });
        var provider = new ServiceCollection()
            .AddSingleton(root).AddSingleton(profiles).AddSingleton(new CapacitorServer(server.Url!, root, profiles))
            .AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None).BuildValidated();
        return (provider.GetRequiredService<ICapacitorHttpClient>(), profiles, provider);
    }
}

public class ServerSessionHttpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Responder_posts_the_payload_and_maps_status_codes() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/ok").UsingPost().WithBody(b => b!.Contains("\"behavior\":\"allow\"")))
              .RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/gone").UsingPost()).RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/bad").UsingPost()).RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"count mismatch"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var respond = ServerSessionHttp.Responder(http, profiles);
        var allow = new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow };

        await Assert.That((await respond("s1", "ok", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.Applied);
        await Assert.That((await respond("s1", "gone", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.NotPending);
        var rejected = await respond("s1", "bad", allow, CancellationToken.None);
        await Assert.That(rejected.Kind).IsEqualTo(ServerRespondKind.Rejected);
        await Assert.That(rejected.Reason).Contains("count mismatch");
    }

    [Test]
    public async Task Responder_lets_the_callers_own_cancellation_escape_a_pending_bad_request_read() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/slow").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"count mismatch"}""").WithDelay(TimeSpan.FromSeconds(3)));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var respond = ServerSessionHttp.Responder(http, profiles);
        var allow = new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => respond("s1", "slow", allow, cts.Token));
    }

    [Test]
    public async Task Responder_without_a_server_or_client_is_unreachable_not_a_throw() {
        var outcome = await ServerSessionHttp.Responder(null, null)("s1", "r1", new PermissionResponsePayload { Behavior = "allow" }, CancellationToken.None);
        await Assert.That(outcome.Kind).IsEqualTo(ServerRespondKind.Unreachable);
    }

    [Test]
    public async Task Session_events_reader_asks_from_the_given_count_and_distinguishes_not_found() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/events").WithParam("from", "3").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"last_event_number":9,"events":[{"event_type":"InterruptIssued","event_number":3,"payload":{"request_id":"p1"}}],"next_from":4}"""));
        server.Given(Request.Create().WithPath("/api/sessions/nope/events").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var read = ServerSessionHttp.SessionEventsReader(http, profiles);

        var page = (await read("s1", 3, null, null, CancellationToken.None)).Page!;
        await Assert.That(page.Events![0].EventNumber).IsEqualTo(3);
        await Assert.That(page.NextFrom).IsEqualTo(4);
        await Assert.That(page.LastEventNumber).IsEqualTo(9);
        await Assert.That((await read("nope", 0, null, null, CancellationToken.None)).NotFound).IsTrue();
    }

    [Test]
    public async Task Session_events_reader_asks_for_a_tail_and_reads_where_the_page_starts() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/events").WithParam("from", "0").WithParam("tail", "300").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"last_event_number":9,"events":[{"event_type":"AssistantMessage","event_number":9,"payload":{}}],"first_from":9}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var page = (await ServerSessionHttp.SessionEventsReader(http, profiles)("s1", 0, null, 300, CancellationToken.None)).Page!;
        await Assert.That(page.FirstFrom).IsEqualTo(9);
        await Assert.That(page.Events![0].EventNumber).IsEqualTo(9);
    }

    [Test]
    public async Task Pending_interrupts_reader_reads_the_set_and_distinguishes_not_found() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/interrupts/pending").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""
                      {"last_event_number":41,"next_from":42,"ended":false,"interrupts":[{"request_id":"p1","kind":"permission","is_acp":false,
                       "answerable_over_http":true,"event":{"event_type":"InterruptIssued","event_number":7,"payload":{"request_id":"p1","kind":"permission"}}}]}
                      """));
        server.Given(Request.Create().WithPath("/api/sessions/nope/interrupts/pending").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var read = ServerSessionHttp.PendingInterruptsReader(http, profiles);

        var page = (await read("s1", CancellationToken.None)).Page!;
        await Assert.That(page.NextFrom).IsEqualTo(42);
        await Assert.That(page.Interrupts!.Select(i => i.Event!.EventNumber)).IsEquivalentTo(new long[] { 7 });
        await Assert.That((await read("nope", CancellationToken.None)).NotFound).IsTrue();
    }
}
