using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Recording.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using TUnit.Core.Enums;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>What the sessionEnd hook hands the detached finalizer, which must survive Copilot killing the hook.</summary>
public class CopilotSessionEndHookTests : IDisposable {
    const string Session = "d6c19ec2-5a16-47b2-a311-a59ed6609a25";

    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    CopilotHookCommand Hook(WatcherManager watchers, FixedCapacitorHttpClient client) =>
        new(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
            HostedAgent.Terminal, client, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory));

    WatcherManager Watchers(FakeProcessStarter starter, FixedCapacitorHttpClient client) =>
        new(Config.Root, Resolutions.At(_server.Url!, Config.Root), client, starter, WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);

    static StringReader EndPayload() =>
        new(new JsonObject { ["sessionId"] = Session, ["reason"] = "user_exit", ["timestamp"] = 1793846402847 }.ToJsonString());

    /// <summary>Copilot runs every deferred sessionEnd hook under one shared shutdown budget, so a hook that spends it on
    /// network work leaves the next sessions' hooks aborted or skipped. Once the finalizer runs, the hook sends nothing.</summary>
    [Test]
    public async Task a_started_finalizer_carries_the_end_and_the_hook_sends_nothing() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var client  = new FixedCapacitorHttpClient();
        var starter = FakeProcessStarter.Running(_ => Process.GetCurrentProcess());

        var exit = await Hook(Watchers(starter, client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries).IsEmpty();

        var handed = JsonNode.Parse(starter.Seen!.ArgumentList.Last())!;
        await Assert.That(handed["reason"]?.GetValue<string>()).IsEqualTo("user_exit");
        await Assert.That(handed["ended_at"]?.GetValue<string>()).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1793846402847).ToString("O"));
    }

    [Test]
    public async Task a_finalizer_that_cannot_start_leaves_the_hook_to_post_the_body_it_would_have_carried() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var client  = new FixedCapacitorHttpClient();
        var starter = FakeProcessStarter.Refusing();

        await Hook(Watchers(starter, client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

        var posted = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost()).Single().RequestMessage.Body;
        await Assert.That(starter.Seen!.ArgumentList.Last()).IsEqualTo(posted);
    }

    /// <summary>The hook records the watcher whose end it took over, asks it to stop and leaves its going to the
    /// finalizer. One that ignores the request is still running when the hook returns: a hook that waited would have
    /// killed it.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task the_hook_asks_the_watcher_to_stop_without_waiting_for_it() {
        var paths  = WatcherPaths.FromEnvironment(Config.Root);
        var marker = Config.PathTo("asked");
        var ready  = Config.PathTo("ready");
        Directory.CreateDirectory(paths.Directory);

        using var watcher = Process.Start(new ProcessStartInfo("sh", ["-c", $"trap 'touch \"{marker}\"' TERM; touch \"{ready}\"; while :; do sleep 0.1; done"]) { UseShellExecute = false })!;
        try {
            await Assert.That(await WaitForFile(ready)).IsTrue();
            await File.WriteAllTextAsync(paths.PidFile(Session.Replace("-", "")), $"{watcher.Id}\n{PidIdentity.Capture(watcher.Id)}");

            var client = new FixedCapacitorHttpClient();
            await Hook(Watchers(FakeProcessStarter.Running(_ => Process.GetCurrentProcess()), client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

            await Assert.That(watcher.HasExited).IsFalse();
            await Assert.That(await WaitForFile(marker)).IsTrue();
            await Assert.That(CopilotEndHandoff.CoversCurrentWatcher(paths, Session.Replace("-", ""))).IsTrue();
        } finally {
            watcher.Kill();
        }
    }

    static async Task<bool> WaitForFile(string path) {
        for (var i = 0; i < 50 && !File.Exists(path); i++) await Task.Delay(100);

        return File.Exists(path);
    }

    /// <summary>A resume after the profile's repository list changed has to be judged afresh, and nothing is posted for
    /// either kind.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task an_end_lifts_an_exclusion_and_no_persons_disable(bool excluded) {
        var marker = Session.Replace("-", "");
        if (excluded) DisabledSessions.Exclude(marker, Config.Root); else DisabledSessions.Mark(marker, Config.Root);

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var client   = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, client, FakeProcessStarter.Running(_ => null), WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);
        using var stdin = new StringReader(new JsonObject { ["sessionId"] = Session, ["reason"] = "user_exit" }.ToJsonString());

        await new CopilotHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, client, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(stdin, ["--event", "sessionEnd"]);

        await Assert.That(DisabledSessions.IsDisabled(marker, Config.Root)).IsEqualTo(!excluded);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task an_end_with_no_timestamp_still_names_when_it_ended() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var client   = new FixedCapacitorHttpClient();
        var watchers = new WatcherManager(Config.Root, profiles, client, FakeProcessStarter.Running(_ => null), WatcherPaths.FromEnvironment(Config.Root), new FakeWatcherSpawner(), TimeProvider.System);
        using var stdin = new StringReader(new JsonObject { ["sessionId"] = Session, ["reason"] = "user_exit" }.ToJsonString());

        await new CopilotHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, client, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(stdin, ["--event", "sessionEnd"]);

        var posted = JsonNode.Parse(_server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost()).Single().RequestMessage.Body!)!;
        await Assert.That(posted["ended_at"]?.GetValue<string>()).IsNotNull();
    }
}
