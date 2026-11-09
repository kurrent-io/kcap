using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The session-start relay: a Claude hook tells the daemon its session started, and the bridge hands
/// the attributed agent to the orchestrator.
[NotInParallel(nameof(LocalPermissionBridgeSessionStartTests))]
public class LocalPermissionBridgeSessionStartTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public LocalPermissionBridge Bridge { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public List<string> Seen { get; } = [];

        public Harness(string? attributeTo = "agent-1") {
            Bridge = new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System) {
                AttributeHandler    = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
                SessionStartHandler = Seen.Add,
            };
        }

        public Task StartAsync() => Bridge.StartAsync(CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(object body, string vendor = "claude", string? token = null) {
            var baseUrl = token is null ? Bridge.BaseUrl! : $"http://127.0.0.1:{new Uri(Bridge.BaseUrl!).Port}/{token}";
            return Client.PostAsync($"{baseUrl}/{vendor}/session-start", JsonContent.Create(body));
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); }
    }

    [Test]
    public async Task A_relay_names_the_attributed_agent() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", cwd = "/repo" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo("agent-1");
    }

    /// The hook can outrun the launch that spawned it, and a resumed agent has no transcript to fall
    /// back on, so a notice dropped here would leave its prompt held for good.
    [Test]
    public async Task A_relay_that_beats_the_agents_publication_is_placed_once_it_appears() {
        await using var h = new Harness();
        var calls = 0;
        h.Bridge.AttributeHandler = _ => Interlocked.Increment(ref calls) < 3 ? null : new AttributedAgent("agent-1");
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo("agent-1");
    }

    /// A relay the ladder cannot place is not an error the hook can act on, so it is acknowledged
    /// and dropped rather than refused.
    [Test]
    public async Task An_unattributed_relay_is_acknowledged_and_dropped() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test]
    public async Task A_relay_without_a_session_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { agent_id = "agent-1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test]
    public async Task An_unknown_token_has_no_relay_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1" }, token: "not-the-token");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }
}
