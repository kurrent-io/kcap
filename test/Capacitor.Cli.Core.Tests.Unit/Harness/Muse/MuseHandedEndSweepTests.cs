using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

/// <summary>A handed Muse end no watcher took is moved into the lifecycle spool by the drains that already run, so it
/// is sent even when no hook for its session ever runs again.</summary>
public class MuseHandedEndSweepTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string Session = "01a13694b0ad7ae28bf52af6357a0847";
    const string Named   = """{"hook_event_name":"SessionEnd","reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-2"}""";
    const string Unnamed = """{"hook_event_name":"SessionEnd","reason":"exit"}""";

    WatcherPaths Paths => field ??= new WatcherPaths(Tmp.PathTo("watchers"));
    HookSpool    Spool => field ??= new HookSpool(Tmp.PathTo("spool"), TimeProvider.System);

    const string Server = "https://kcap.example";

    void Hand(string payload, TimeSpan ago, string? server = Server) {
        MuseSessionEndRequest.Write(Paths, Session, payload, server);
        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(Paths, Session, payload), DateTime.UtcNow - ago);
    }

    async Task<List<(string Route, JsonNode Body)>> Spooled() {
        var drained = new List<(string, JsonNode)>();
        await Spool.DrainAllAsync(Session, (r, b) => { drained.Add((r, JsonNode.Parse(b)!)); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        return drained;
    }

    [Test]
    public async Task An_unattended_request_is_spooled_as_the_sessions_end_and_released() {
        Hand(Named, TimeSpan.FromMinutes(10));

        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, TimeProvider.System)).IsEqualTo(1);

        var (route, body) = (await Spooled()).Single();
        await Assert.That(route).IsEqualTo(MuseHandedEndSweep.Route);
        await Assert.That(body["daemon_epoch"]!.GetValue<string>()).IsEqualTo("epoch-2");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Session);
        await Assert.That(body["transcript_path"]).IsNotNull();
        await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsFalse();
    }

    /// <summary>The end would bring back a deleted stream, so it goes unsent, and nothing keeps asking for it.</summary>
    [Test]
    public async Task A_disabled_sessions_request_is_dropped_unsent() {
        var config = new TempConfigRoot();
        using (config) {
            var spool = new HookSpool(config.Root, TimeProvider.System, Server);
            DisabledSessions.Mark(Session, config.Root);
            Hand(Named, TimeSpan.FromMinutes(10));

            await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, spool, Server, Daemons.Store, TimeProvider.System)).IsEqualTo(0);

            await Assert.That(spool.HasBacklog(Session)).IsFalse();
            await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsFalse();
        }
    }

    /// <summary>A request is spooled only into the spool of the server it was handed over for: one for another server is
    /// left for that server's drain, and one written before requests recorded a server is taken by any drain.</summary>
    [Test]
    [Arguments("https://other.example", 0)]
    [Arguments("https://KCAP.example/", 1)]
    [Arguments(null, 1)]
    public async Task A_request_is_spooled_only_for_the_server_it_was_handed_over_for(string? server, int spooled) {
        Hand(Named, TimeSpan.FromMinutes(10), server);

        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, TimeProvider.System)).IsEqualTo(spooled);
        await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsEqualTo(spooled == 0);
    }

    /// <summary>A resume that has started but not yet moved the run to its new epoch holds an end that names no run, as
    /// the fence holds it for a hook or a watcher: posted, it would close the session being resumed.</summary>
    [Test]
    public async Task An_unnamed_request_is_held_while_its_session_is_resuming() {
        ResumeLedgerStore.For(Daemons.Store, "d1").Write(ResumeLedgerEntries.For("agent-1", ResumeState.Resuming) with {
            LaunchEpoch = "epoch-1", RunEpoch = "epoch-1", SessionId = Session
        });
        Hand(Unnamed, TimeSpan.FromMinutes(10));

        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, TimeProvider.System)).IsEqualTo(0);
        await Assert.That(await Spooled()).IsEmpty();
    }

    /// <summary>A live watcher takes a request within a tick, and one that is backing off waits its turn.</summary>
    [Test]
    public async Task A_fresh_or_backing_off_request_is_left_alone() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Hand(Named, TimeSpan.FromMinutes(1));
        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, time)).IsEqualTo(0);
        await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsTrue();

        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(Paths, Session, Named), DateTime.UtcNow.AddMinutes(-10));
        MuseSessionEndRequest.Defer(MuseSessionEndRequest.TryClaim(Paths, Session, "watcher", time)!, time);
        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, time)).IsEqualTo(0);
        await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsTrue();
    }

    /// <summary>An end that names no run is placed by its session: after its agent was resumed it may be an earlier
    /// run's, and is dropped rather than spooled; before that it is spooled.</summary>
    [Test]
    [Arguments("epoch-2", 0)]
    [Arguments("epoch-1", 1)]
    public async Task An_unnamed_request_is_held_once_its_session_was_resumed(string runEpoch, int spooled) {
        ResumeLedgerStore.For(Daemons.Store, "d1").Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running) with {
            LaunchEpoch = "epoch-1", RunEpoch = runEpoch, SessionId = Session
        });
        Hand(Unnamed, TimeSpan.FromMinutes(10));

        await Assert.That(MuseHandedEndSweep.SpoolDue(Paths, Spool, Server, Daemons.Store, TimeProvider.System)).IsEqualTo(spooled);
        await Assert.That(MuseSessionEndRequest.Exists(Paths, Session)).IsFalse();
    }
}
