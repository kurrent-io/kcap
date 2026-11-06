using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Recording.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class RecorderHousekeepingTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempHome] public required TempHome Home { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string Session = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    const string Child = "1199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    static readonly string ChildAgent = Guid.Parse(Child).ToString("N");

    /// <summary>A child the parent log never announces is held back this many scans, then started on the next.</summary>
    const int ScansUntilAnUnannouncedChildStarts = 4;

    readonly FakeRecorderHost _host = new();

    string CreateParentLog() => Tmp.CreateFile("muse/session.jsonl", "");

    string CreateChildLog() => Tmp.CreateFile($"muse/subagent/{Child}/session.jsonl", "");

    RecorderHousekeeping MuseHousekeeping(string transcript, string serverUrl = "http://127.0.0.1:1", string vendor = "muse", Action<string>? log = null, Action? beat = null) => new(
        new RecordingSource(Session, null, transcript, vendor, null), skipTitle: false, parentPid: null, _host,
        Config.Root, Resolutions.At(serverUrl, Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(),
        new GitProviderRouter(), TimeProvider.System, new AccountStore(Config.PathTo("accounts")), Home, log ?? (_ => { }), beat ?? (() => { }));

    static async Task ScanAsync(RecorderHousekeeping housekeeping, string transcript, int times) {
        for (var i = 0; i < times; i++) await housekeeping.AfterDrainAsync(new WatchState(), transcript, [], CancellationToken.None);
    }

    static WireMockServer SubagentStartAnswers(int status) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost()).RespondWith(Response.Create().WithStatusCode(status));

        return server;
    }

    /// <summary>A stop that waited for the thread pool would let the recorder take another tick after the end was handed over.</summary>
    [Test]
    public async Task A_handed_Muse_end_stops_the_run_on_the_calling_thread() {
        var transcript   = CreateParentLog();
        var housekeeping = MuseHousekeeping(transcript);
        MuseSessionEndRequest.Write(WatcherPaths.FromEnvironment(Config.Root), Session, """{"reason":"other"}""");

        var thread = Environment.CurrentManagedThreadId;
        await ScanAsync(housekeeping, transcript, times: 1);

        await Assert.That(_host.StopThreads).IsEquivalentTo([thread]);
        await Assert.That(housekeeping.HandedMuseEnd).IsNotNull();
    }

    /// <summary>A host guards the log in a recorder's context, and this one comes to it separately.</summary>
    [Test]
    public async Task A_log_that_throws_does_not_fail_housekeeping() {
        var transcript   = CreateParentLog();
        var housekeeping = MuseHousekeeping(transcript, log: _ => throw new InvalidOperationException("the log is broken"));
        MuseSessionEndRequest.Write(WatcherPaths.FromEnvironment(Config.Root), Session, """{"reason":"other"}""");

        await ScanAsync(housekeeping, transcript, times: 1);

        await Assert.That(housekeeping.HandedMuseEnd).IsNotNull();
    }

    [Test]
    public async Task A_beat_that_throws_does_not_fail_housekeeping() {
        var housekeeping = MuseHousekeeping(CreateParentLog(), vendor: "claude", beat: () => throw new InvalidOperationException("the beat is broken"));
        var state        = new WatchState();

        await housekeeping.BeforeDrainAsync(state, CancellationToken.None);

        await Assert.That(state.LastHarnessTitleProbe).IsNotEqualTo(default(DateTimeOffset));
    }

    [Test]
    public async Task A_child_that_registered_starts_as_its_own_source_and_is_left_for_the_host_to_stop() {
        using var server = SubagentStartAnswers(200);
        var transcript   = CreateParentLog();
        var childLog     = CreateChildLog();
        var housekeeping = MuseHousekeeping(transcript, server.Url!);

        await ScanAsync(housekeeping, transcript, ScansUntilAnUnannouncedChildStarts);

        await Assert.That(_host.Started).IsEquivalentTo([new RecordingSource(Session, ChildAgent, childLog, "muse", null)]);
        await Assert.That(housekeeping.SpawnedChildKeys).IsEquivalentTo([$"{Session}-{ChildAgent}"]);
    }

    /// <summary>Content streamed ahead of its registration has no subagent to land on.</summary>
    [Test]
    public async Task A_refused_registration_starts_no_child() {
        using var server = SubagentStartAnswers(400);
        var transcript = CreateParentLog();
        CreateChildLog();
        var housekeeping = MuseHousekeeping(transcript, server.Url!);

        await ScanAsync(housekeeping, transcript, ScansUntilAnUnannouncedChildStarts);

        await Assert.That(_host.Started).IsEmpty();
        await Assert.That(housekeeping.SpawnedChildKeys).IsEmpty();
    }

    [Test]
    public async Task A_child_the_host_could_not_start_is_not_left_for_it_to_stop() {
        using var server = SubagentStartAnswers(200);
        _host.StartsChild = _ => false;
        var transcript = CreateParentLog();
        CreateChildLog();
        var housekeeping = MuseHousekeeping(transcript, server.Url!);

        await ScanAsync(housekeeping, transcript, ScansUntilAnUnannouncedChildStarts);

        await Assert.That(_host.Started.Count).IsEqualTo(1);
        await Assert.That(housekeeping.SpawnedChildKeys).IsEmpty();
    }
}
