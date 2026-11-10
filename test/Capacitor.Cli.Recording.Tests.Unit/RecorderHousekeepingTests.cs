using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Time.Testing;
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

    // In segments, as the scan builds the path it starts the child with: a literal "/" stays one on Windows.
    string CreateChildLog() => Tmp.CreateFile(Path.Combine("muse", "subagent", Child, "session.jsonl"), "");

    RecorderHousekeeping MuseHousekeeping(
        string transcript, string serverUrl = "http://127.0.0.1:1", string vendor = "muse", Action<string>? log = null, Action? beat = null,
        Func<Func<Task>, Task>? carry = null) => new(
        new RecordingSource(Session, null, transcript, vendor, null), skipTitle: false, parentPid: null, _host,
        Config.Root, Resolutions.At(serverUrl, Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(),
        new GitProviderRouter(), TimeProvider.System, new AccountStore(Config.PathTo("accounts")), Home, log ?? (_ => { }), beat ?? (() => { })) {
        Carry = carry,
    };

    static readonly RepositoryPayload Pr = new() { Host = "github.com", Owner = "acme", RepoName = "widgets", PrNumber = 7, PrUrl = "https://github.com/acme/widgets/pull/7" };

    sealed class FixedTitleStore(StoreTitle title) : IHarnessTitleStore {
        public bool RecordsChangeTime => false;

        public StoreTitle? Read() => title;
    }

    static WireMockServer Answering(string path, int status) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(status));

        return server;
    }

    /// <summary>A daemon counts a session's posts with its hub calls, and refuses them once the session is disabled.</summary>
    [Test]
    public async Task A_refused_carry_sends_no_pull_request_link() {
        using var server       = Answering($"/api/sessions/{Session}/pull-requests", 200);
        var       housekeeping = MuseHousekeeping(CreateParentLog(), server.Url!, vendor: "claude", carry: _ => throw new InvalidOperationException("silenced"));

        await Assert.That(await housekeeping.PostLinkedPullRequestAsync(Session, Pr, "created in the session", CancellationToken.None)).IsFalse();
        await Assert.That(server.LogEntries).IsEmpty();
    }

    /// <summary>The count waits on the post itself, so the post runs inside it.</summary>
    [Test]
    public async Task A_pull_request_link_is_sent_inside_the_carry() {
        using var server       = Answering($"/api/sessions/{Session}/pull-requests", 200);
        var       sentInside   = -1;
        var       housekeeping = MuseHousekeeping(CreateParentLog(), server.Url!, vendor: "claude", carry: async post => {
            await post();
            sentInside = server.LogEntries.Count;
        });

        await Assert.That(await housekeeping.PostLinkedPullRequestAsync(Session, Pr, "created in the session", CancellationToken.None)).IsTrue();
        await Assert.That(sentInside).IsEqualTo(1);
    }

    /// <summary>A title post that failed may have reached the server first, so the count hears of it as one that threw.</summary>
    [Test]
    public async Task A_failed_title_post_reads_as_unanswered_to_the_carry() {
        using var server       = Answering("/hooks/harness-title", 500);
        Exception? heard       = null;
        var       housekeeping = MuseHousekeeping(CreateParentLog(), server.Url!, vendor: "claude", carry: async post => {
            try {
                await post();
            } catch (Exception ex) {
                heard = ex;

                throw;
            }
        });
        var state = new WatchState { TitleTracker = new HarnessTitleTracker(recordsChangeTime: false) };

        await housekeeping.PostHarnessTitleAsync(new FixedTitleStore(new StoreTitle("A name", HarnessTitleKind.Rename, null)), Session, state, CancellationToken.None, () => { });

        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        await Assert.That(heard).IsNotNull();
        await Assert.That(state.HarnessTitleSeen).IsFalse();
    }

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

    RecorderHousekeeping ClaudeHousekeeping(string? agentId, IRecorderHost host, TimeProvider time) => new(
        new RecordingSource(Session, agentId, Tmp.PathTo("session.jsonl"), "claude", null), skipTitle: true, parentPid: null, host,
        Config.Root, Resolutions.At("http://127.0.0.1:1", Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(),
        new GitProviderRouter(), time, new AccountStore(Config.PathTo("accounts")), Home, _ => { }, () => { });

    /// <summary>A session may run no hook that posts its workspace span, so its recorder delivers the spool, at
    /// most once a throttle; a child's recorder leaves it to the session's.</summary>
    [Test]
    public async Task A_session_drains_the_workspace_spool_once_a_throttle_and_a_child_never_does() {
        var time      = new FakeTimeProvider();
        var childHost = new FakeRecorderHost();
        var session   = ClaudeHousekeeping(null, _host, time);
        var child     = ClaudeHousekeeping(ChildAgent, childHost, time);

        await ScanAsync(session, Tmp.PathTo("session.jsonl"), times: 2);
        await ScanAsync(child, Tmp.PathTo("session.jsonl"), times: 2);
        await Assert.That(_host.WorkspaceDrains).IsEqualTo(1);

        time.Advance(WorkspaceSpool.DrainThrottle);
        await ScanAsync(session, Tmp.PathTo("session.jsonl"), times: 1);
        await ScanAsync(child, Tmp.PathTo("session.jsonl"), times: 1);

        await Assert.That(_host.WorkspaceDrains).IsEqualTo(2);
        await Assert.That(childHost.WorkspaceDrains).IsEqualTo(0);
    }
}
