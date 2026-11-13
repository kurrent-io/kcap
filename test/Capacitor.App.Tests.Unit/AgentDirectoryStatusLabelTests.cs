using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The rail's status label over a row the session directory merged.
public class AgentDirectoryStatusLabelTests {
    const string Server = "http://localhost:9999"; // FakeDaemonClientService.Snap's default ServerUrl

    static AgentStatusDto LocalAgent(string id, string? sessionId = null, string vendor = "claude", string status = "Running") => new(
        Id: id, Kind: "agent", Vendor: vendor, RepoPath: "/r", Status: status,
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, SessionId: sessionId);

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerSessionsService Sessions, FakeTimeProvider Time, AgentDirectory Dir)
        BuildWithSessions() {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var sessions = new FakeServerSessionsService();
        var time = new FakeTimeProvider();
        var dir = new AgentDirectory(
            local, remote, new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p,
            "m1", Server, time, sessions);
        return (local, remote, sessions, time, dir);
    }

    static RepoSessionDto Listed(string id, string status = "ended", string owner = "me", string ownerName = "Alexey") => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = id,
        Owner = new() { UserId = owner, DisplayName = ownerName, AvatarUrl = $"https://avatars.example/{owner}" },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch : null,
    };

    /// The local daemon connected and reporting the app's own server.
    static void LocalConnected(FakeDaemonClientService local) {
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
    }

    /// A live session of the viewer's: in the unfiltered listing and in the Mine one.
    static void MineLive(FakeServerSessionsService sessions, string id) {
        var row = Listed(id, "active");
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);
    }

    /// The server lists a stopped session live until it folds the end, and the daemon unpublishes
    /// the finished agent long before that. The row standing for the session reads ended like the
    /// workspace header, not Running, so the open workspace can hand over to it at once.
    [Test]
    public async Task A_finished_local_agents_session_reads_ended_while_the_listing_still_says_live() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "0D1E2F30-4455-6677-8899-AABBCCDDEEFF"));
        MineLive(sessions, "0d1e2f30445566778899aabbccddeeff");

        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "0D1E2F30-4455-6677-8899-AABBCCDDEEFF", status: "Completed"));
        await Assert.That(dir.Rows.Lookup("server:0d1e2f30445566778899aabbccddeeff").Value.IsEndedSession).IsTrue();

        local.Agents.RemoveKey("a1");
        var server = dir.Rows.Lookup("server:0d1e2f30445566778899aabbccddeeff").Value;
        await Assert.That(server.IsEndedSession).IsTrue();
        await Assert.That(Capacitor.App.ViewModels.SessionStatusDots.Label(server)).IsEqualTo("Done");
    }
}
