using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>What the sessionEnd hook hands the detached finalizer, which must survive Copilot killing the hook.</summary>
public class CopilotSessionEndHookTests : IDisposable {
    const string Session = "d6c19ec2-5a16-47b2-a311-a59ed6609a25";

    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task the_finalizer_starts_before_any_post_carrying_the_body_the_hook_posts() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var requestsAtSpawn = -1;
        var starter = FakeProcessStarter.Running(_ => {
            requestsAtSpawn = _server.LogEntries.Count;
            return null;
        });

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var client   = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, client, starter, WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);

        var payload = new JsonObject { ["sessionId"] = Session, ["reason"] = "user_exit", ["timestamp"] = 1793846402847 };

        await new CopilotHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, client, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(new StringReader(payload.ToJsonString()), ["--event", "sessionEnd"]);

        await Assert.That(requestsAtSpawn).IsEqualTo(0);

        var posted = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost()).Single().RequestMessage.Body;
        await Assert.That(starter.Seen!.ArgumentList.Last()).IsEqualTo(posted);
        await Assert.That(posted).Contains("user_exit");
    }
}
