using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The recorder as the daemon composes it, against a real hub: its own connection, the real housekeeping, and
/// the order a session's last calls reach the server in.</summary>
[ParallelLimiter<SubprocessLimit>]
public class DaemonRecorderHubTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Prompt   = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply    = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempHome] public required TempHome Home { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    DaemonRecorder Recorder(RecordingHubHost hub, ServerConnection main) {
        ServerVersionStore.Set(hub.Url, RecordingServerFloor.MinimumVersion, Config.Root, TimeProvider.System);

        return DaemonRecorder.Compose(
            DaemonConfigFor(hub), new CapacitorServer(hub.Url, Config.Root, Resolutions.None(Config.Root)), main, new FixedCapacitorHttpClient(),
            TestHarnesses.Under(Home), NullLoggerFactory.Instance, TimeProvider.System);
    }

    DaemonConfig DaemonConfigFor(RecordingHubHost hub) => new() {
        Name = "test", ServerUrl = hub.Url, ConfigRoot = Config.Root, Profiles = Resolutions.None(Config.Root),
        Store = Daemons.Store, Home = Home, RecordHostedClaude = true, Binaries = TestBinaries.None,
    };

    /// <summary><c>WatcherConnect</c> takes a connection out of the UI topics, so a recorder on the daemon's main
    /// connection would silently cut the daemon off from its owner's. Nothing on the hub would say so.</summary>
    [Test]
    public async Task The_daemons_main_connection_keeps_its_topics_while_recording() {
        await using var hub  = await RecordingHubHost.StartAsync();
        await using var main = new ServerConnection(DaemonConfigFor(hub), new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        using var       cts  = new CancellationTokenSource(BoundedWait.Deadline);
        await main.ConnectAsync(cts.Token);
        await hub.WaitForAsync(h => h.DaemonConnections.Count == 1, "the daemon's own connect");

        await using var recorder = Recorder(hub, main);
        var source = new RecordedSource("agent-1", SessionA, null, Tmp.CreateFile("t.jsonl", ""), "claude", null, false);
        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None)).IsTrue();
        await hub.WaitForAsync(h => h.RecordingConnections.Count == 1, "the recorder's connect");

        await Assert.That(hub.RecordingConnections.Overlaps(hub.DaemonConnections)).IsFalse();
        await recorder.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
    }

    /// <summary>The other way a session ends: the server asks, and waits for the drain before it writes the end.</summary>
    [Test]
    public async Task A_server_stop_drains_the_source_and_lets_it_go() {
        await using var hub  = await RecordingHubHost.StartAsync();
        await using var main = new ServerConnection(DaemonConfigFor(hub), new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        await using var recorder = Recorder(hub, main);
        var source = new RecordedSource("agent-1", SessionA, null, Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "claude", null, true);

        await recorder.TryRecordAsync(source, CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains($"connect {SessionA}"), "the connect");
        await hub.PushStopSourceAsync(SessionA);
        await hub.WaitForAsync(h => h.Calls.Contains($"detach {SessionA}"), "the detach");
        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0);

        var calls = hub.Calls.ToList();

        await Assert.That(calls.IndexOf($"drain {SessionA}")).IsGreaterThan(calls.IndexOf("batch 0"));
        await Assert.That(calls.IndexOf("batch 0")).IsGreaterThanOrEqualTo(0);
    }

    /// <summary>The whole way down, against a hub: a session's lines, its drain, and only then its detach. A finish that
    /// catches the first connect unanswered drains on a second one, so only the order of these is the point.</summary>
    [Test]
    public async Task A_finished_source_reaches_the_server_drains_and_detaches() {
        await using var hub  = await RecordingHubHost.StartAsync();
        await using var main = new ServerConnection(DaemonConfigFor(hub), new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        await using var recorder = Recorder(hub, main);
        var source = new RecordedSource("agent-1", SessionA, null, Tmp.CreateFile("t.jsonl", $"{Prompt}\n{Reply}\n"), "claude", null, true);

        await recorder.TryRecordAsync(source, CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains($"connect {SessionA}"), "the connect");
        await recorder.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
        await hub.WaitForAsync(h => h.Calls.Contains($"detach {SessionA}"), "the detach");

        var calls = hub.Calls.ToList();

        await Assert.That(calls.IndexOf("batch 0")).IsGreaterThan(calls.IndexOf($"connect {SessionA}"));
        await Assert.That(calls.IndexOf($"drain {SessionA}")).IsGreaterThan(calls.IndexOf("batch 0"));
        await Assert.That(calls.IndexOf($"detach {SessionA}")).IsGreaterThan(calls.IndexOf($"drain {SessionA}"));
    }
}
