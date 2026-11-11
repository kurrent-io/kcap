using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;
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

    HookSpool Spool() => new(Config.Root, TimeProvider.System, _server.Url);


    string SpooledEnds() =>
        Directory.Exists(Spool().DirectoryPath) ? string.Concat(Directory.GetFiles(Spool().DirectoryPath).Select(File.ReadAllText)) : "";

    /// <summary>Copilot runs every deferred sessionEnd hook under one shared shutdown budget, so a hook that spends it on
    /// network work leaves the next sessions' hooks aborted or skipped. Its end goes to the spool first, where any later
    /// kcap process delivers it if the finalizer does not, and the hook sends nothing.</summary>
    [Test]
    public async Task a_started_finalizer_finds_the_end_already_spooled_and_the_hook_sends_nothing() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var client  = new FixedCapacitorHttpClient();
        var starter = FakeProcessStarter.Running(_ => Process.GetCurrentProcess());

        var exit = await Hook(Watchers(starter, client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(SpooledEnds()).Contains("session-end/copilot");
        await Assert.That(SpooledEnds()).Contains("user_exit");
        await Assert.That(CopilotSpanClose.Pending(WatcherPaths.FromEnvironment(Config.Root), _server.Url)).Contains(Session.Replace("-", ""));
        await Assert.That(starter.Seen!.ArgumentList.Count).IsEqualTo(3);
    }

    [Test]
    public async Task a_finalizer_that_cannot_start_leaves_the_hook_to_post_the_end_itself() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var client = new FixedCapacitorHttpClient();

        await Hook(Watchers(FakeProcessStarter.Refusing(), client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

        var posted = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost()).Single().RequestMessage.Body;
        await Assert.That(posted).Contains("user_exit");
    }

    /// <summary>A pending end goes only on a new run: a notification proves nothing about it, a sessionStart does.</summary>
    [Test]
    [Arguments("notification", true)]
    [Arguments("sessionStart", false)]
    public async Task a_pending_end_outlives_every_hook_but_a_new_run(string eventName, bool kept) {
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        var canonical = Session.Replace("-", "");
        CopilotPendingEnd.Write(paths, canonical, "{}", _server.Url, runtimePid: null, Config.CreateFile("events.jsonl", ""), TimeProvider.System);

        var client  = new FixedCapacitorHttpClient();
        var payload = new JsonObject { ["sessionId"] = Session, ["message"] = "m", ["notification_type"] = "idle_prompt" }.ToJsonString();
        await Hook(Watchers(FakeProcessStarter.Refusing(), client), client).Handle(new StringReader(payload), ["--event", eventName]);

        await Assert.That(CopilotPendingEnd.SessionsWithPending(paths).Contains(canonical)).IsEqualTo(kept);
    }

    /// <summary>An end the spool cannot hold travels with the finalizer instead, so one owner always has it.</summary>
    [Test]
    public async Task a_hook_that_cannot_spool_its_end_hands_it_to_the_finalizer() {
        await File.WriteAllTextAsync(Config.PathTo("spool"), "not a directory");

        var client  = new FixedCapacitorHttpClient();
        var starter = FakeProcessStarter.Running(_ => Process.GetCurrentProcess());
        await Hook(Watchers(starter, client), client).Handle(EndPayload(), ["--event", "sessionEnd"]);

        await Assert.That(starter.Seen!.ArgumentList.Last()).Contains("user_exit");
    }

    /// <summary>The hook asks the watcher to stop and leaves its going to the finalizer. One that ignores the request is
    /// still running when the hook returns: a hook that waited would have killed it.</summary>
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
