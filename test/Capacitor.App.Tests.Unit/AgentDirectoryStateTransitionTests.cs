using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using RepoSessionDto = Capacitor.Remote.Models.RepoSessionDto;

namespace Capacitor.App.Tests.Unit;

public class AgentDirectoryStateTransitionTests {
    const string Server = "http://localhost:9999";
    const string InitialWorktree = "/r/.capacitor/worktrees/local";
    const string UpdatedWorktree = "/updated/.capacitor/worktrees/local";

    static AgentStatusDto Local(string session = "S", string status = "Running", string repo = "/r", string? worktree = InitialWorktree,
            string branch = "daemon-initial") => new(
        "local", "agent", "claude", repo, status, null, null, null, DateTime.UnixEpoch, null, null,
        SessionId: session, WorktreePath: worktree, WorkLocation: "owned", Branch: branch);

    static AgentInstanceDto Remote(string session = "S", string repo = "/r") => new() {
        AgentId = "remote", SessionId = session, Status = "Running", DaemonName = "elsewhere", OwnerUserId = "me",
        Vendor = "claude", RepoPath = repo,
    };

    static RepoSessionDto Listed(string id = "S", string status = "active", string cwd = "/r", string branch = "listed") => new() {
        SessionId = id, Status = status, Cwd = cwd, Branch = branch, AccessLevel = "full", Vendor = "claude", Title = "Recorded title",
        Owner = new() { UserId = "me", DisplayName = "Original owner" },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        EndedAt = status == "active" ? null : DateTimeOffset.UnixEpoch,
    };

    static void Connect(FakeDaemonClientService local) {
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: Server));
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
    }

    static void Publish(FakeServerSessionsService sessions, RepoSessionDto listing) {
        if (listing.Status == "active") {
            sessions.LiveCache.AddOrUpdate(listing);
            sessions.MineLiveCache.AddOrUpdate(listing);
        } else sessions.EndedCache.AddOrUpdate(listing);
    }

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerSessionsService Sessions,
            FakeTimeProvider Time, AgentDirectory Directory) Build(string phase) {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var sessions = new FakeServerSessionsService();
        var time = new FakeTimeProvider();
        var directory = new AgentDirectory(local, remote, new FakeServerLane(), new RepoIdentityResolver(_ => null), path => path,
            "machine", Server, time, sessions);
        Connect(local);
        remote.CurrentSubject.OnNext(true);
        sessions.ViewerIdsSubject.OnNext(new HashSet<string> { "me" });
        Publish(sessions, phase == "restored" ? Listed(status: "ended", cwd: "/r/.capacitor/worktrees/restored") : Listed());
        if (phase is "local" or "finished") local.Agents.AddOrUpdate(Local());
        if (phase is "remote" or "handover") remote.Cache.AddOrUpdate(Remote());
        if (phase == "finished") {
            local.Agents.AddOrUpdate(Local(status: "Completed"));
            local.Agents.RemoveKey("local");
        }
        if (phase == "handover") remote.Cache.RemoveKey("remote");
        return (local, remote, sessions, time, directory);
    }

    [Test]
    [Arguments("local", AgentOrigin.Local)]
    [Arguments("local", AgentOrigin.Remote)]
    [Arguments("local", AgentOrigin.Server)]
    [Arguments("remote", AgentOrigin.Local)]
    [Arguments("remote", AgentOrigin.Remote)]
    [Arguments("remote", AgentOrigin.Server)]
    [Arguments("handover", AgentOrigin.Local)]
    [Arguments("handover", AgentOrigin.Remote)]
    [Arguments("handover", AgentOrigin.Server)]
    [Arguments("finished", AgentOrigin.Local)]
    [Arguments("finished", AgentOrigin.Remote)]
    [Arguments("finished", AgentOrigin.Server)]
    [Arguments("recorded", AgentOrigin.Local)]
    [Arguments("recorded", AgentOrigin.Remote)]
    [Arguments("recorded", AgentOrigin.Server)]
    [Arguments("restored", AgentOrigin.Local)]
    [Arguments("restored", AgentOrigin.Remote)]
    [Arguments("restored", AgentOrigin.Server)]
    public async Task Every_session_state_accepts_each_source_update_without_mixing_field_ownership(string phase, AgentOrigin update) {
        var (local, remote, sessions, _, directory) = Build(phase);
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        if (update == AgentOrigin.Local) local.Agents.AddOrUpdate(Local(repo: "/updated", worktree: UpdatedWorktree, branch: "daemon-new"));
        if (update == AgentOrigin.Remote) remote.Cache.AddOrUpdate(Remote(repo: "/registry-new"));
        var updatedListing = Listed(status: phase == "restored" ? "ended" : "active", cwd: "/different/.capacitor/worktrees/listed-new", branch: "listed-new")
            with { Owner = new() { UserId = "me", DisplayName = "New owner" }, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1) };
        if (update == AgentOrigin.Server) Publish(sessions, updatedListing);

        var key = update switch {
            AgentOrigin.Local => "local:local",
            AgentOrigin.Remote => "remote:remote",
            _ => phase switch { "local" => "local:local", "remote" => "remote:remote", _ => "server:S" },
        };
        await Assert.That(directory.Rows.Lookup(key).HasValue).IsTrue();
        var row = directory.Rows.Lookup(key).Value;
        await Assert.That(row.OwnerUserId).IsEqualTo("me");
        await Assert.That(row.OwnerName).IsEqualTo(update == AgentOrigin.Server ? "New owner" : "Original owner");
        await Assert.That(row.OwnerIsViewer).IsTrue();
        await Assert.That(row.LastActivityAt).IsEqualTo(update == AgentOrigin.Server ? DateTime.UnixEpoch.AddHours(1) : DateTime.UnixEpoch);
        await Assert.That(row.AccessLevel).IsEqualTo(row.Origin == AgentOrigin.Server ? "full" : null);
        await Assert.That(row.Status).IsEqualTo(row.Origin == AgentOrigin.Server && (phase is "finished" or "restored") ? "Completed" : "Running");
        if (update == AgentOrigin.Local) {
            await Assert.That(row.RepoPath).IsEqualTo("/updated");
            await Assert.That(row.WorktreePath).IsEqualTo(UpdatedWorktree);
            await Assert.That(row.Branch).IsEqualTo("listed");
        } else if (update == AgentOrigin.Remote) {
            await Assert.That(row.RepoPath).IsEqualTo("/registry-new");
            await Assert.That(row.WorktreePath).IsNull();
            await Assert.That(row.Branch).IsEqualTo("listed");
        } else {
            var checkout = phase switch { "recorded" or "restored" => "/different", _ => "/r" };
            var worktree = phase switch { "local" or "finished" => InitialWorktree, "recorded" or "restored" => "/different/.capacitor/worktrees/listed-new", _ => null };
            await Assert.That(row.RepoPath).IsEqualTo(checkout);
            await Assert.That(row.WorktreePath).IsEqualTo(worktree);
            await Assert.That(row.Branch).IsEqualTo(phase == "finished" ? "daemon-initial" : "listed-new");
        }

        if (update is AgentOrigin.Local or AgentOrigin.Remote) {
            local.Agents.RemoveKey("local");
            remote.Cache.RemoveKey("remote");
            var recorded = directory.Rows.Lookup("server:S").Value;
            var localCheckout = update == AgentOrigin.Local || phase is "local" or "finished";
            await Assert.That(recorded.RepoPath).IsEqualTo(update == AgentOrigin.Local ? "/updated" : localCheckout ? "/r" : "/registry-new");
            await Assert.That(recorded.WorktreePath).IsEqualTo(update == AgentOrigin.Local ? UpdatedWorktree : localCheckout ? InitialWorktree : null);
            await Assert.That(recorded.Branch).IsEqualTo(update == AgentOrigin.Local ? "daemon-new" : localCheckout ? "daemon-initial" : "listed");
            await Assert.That(recorded.IsEndedSession).IsEqualTo(phase == "restored" || phase == "finished" && update == AgentOrigin.Remote);
            await Assert.That(directory.RowForSession("S")).IsEqualTo(("S", AgentOrigin.Server));
        }
    }

    [Test]
    [Arguments("handover")]
    [Arguments("finished")]
    public async Task Presentation_deadlines_transition_on_the_timer_without_a_source_refresh(string phase) {
        var (_, _, sessions, time, directory) = Build(phase);
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        var duration = phase == "handover" ? AgentDirectory.HandoverGrace : AgentDirectory.FinishedHold;
        time.Advance(duration - TimeSpan.FromMilliseconds(1));
        var before = directory.Rows.Lookup("server:S").Value;
        await Assert.That(before.IsTerminalSession).IsFalse();
        await Assert.That(before.IsEndedSession).IsEqualTo(phase == "finished");
        time.Advance(TimeSpan.FromMilliseconds(1));
        var after = directory.Rows.Lookup("server:S").Value;
        await Assert.That(after.IsTerminalSession).IsTrue();
        await Assert.That(after.IsEndedSession).IsFalse();
        await Assert.That(after.RepoPath).IsEqualTo("/r");
        await Assert.That(after.Branch).IsEqualTo(phase == "finished" ? "daemon-initial" : "listed");
    }

    [Test]
    [Arguments("local")]
    [Arguments("remote")]
    [Arguments("failed")]
    public async Task Cached_or_failed_sources_do_not_renew_the_hosted_grace(string source) {
        var (local, remote, sessions, time, directory) = Build(source == "remote" ? "remote" : "local");
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        if (source == "remote") remote.CurrentSubject.OnNext(false);
        else if (source == "failed") local.Agents.AddOrUpdate(Local(status: "Failed"));
        else local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        time.Advance(AgentDirectory.HandoverGrace);
        await Assert.That(directory.Rows.Lookup("server:S").HasValue).IsFalse();
        local.Agents.RemoveKey("local");
        remote.Cache.RemoveKey("remote");
        await Assert.That(directory.Rows.Lookup("server:S").Value.IsTerminalSession).IsTrue();
    }

    [Test]
    [Arguments(AgentOrigin.Local)]
    [Arguments(AgentOrigin.Remote)]
    public async Task A_current_host_uses_live_metadata_until_a_stale_source_yields_to_the_ended_listing(AgentOrigin source) {
        var (local, remote, sessions, _, directory) = Build(source == AgentOrigin.Local ? "local" : "remote");
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        Publish(sessions, Listed(status: "ended", branch: "ended-branch") with {
            Owner = new() { UserId = "me", DisplayName = "Ended owner" }, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
        });
        var hosted = directory.Rows.Lookup(source == AgentOrigin.Local ? "local:local" : "remote:remote").Value;
        await Assert.That(hosted.OwnerName).IsEqualTo("Original owner");
        await Assert.That(hosted.Branch).IsEqualTo("listed");
        await Assert.That(hosted.LastActivityAt).IsEqualTo(DateTime.UnixEpoch);

        if (source == AgentOrigin.Local) local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        else remote.CurrentSubject.OnNext(false);
        var recorded = directory.Rows.Lookup("server:S").Value;
        await Assert.That(recorded.IsEndedSession).IsTrue();
        await Assert.That(recorded.OwnerName).IsEqualTo("Ended owner");
        await Assert.That(recorded.Branch).IsEqualTo(source == AgentOrigin.Local ? "daemon-initial" : "ended-branch");
        await Assert.That(recorded.LastActivityAt).IsEqualTo(DateTime.UnixEpoch.AddHours(1));
    }

    [Test]
    [Arguments(AgentOrigin.Remote, "Running", true, true)]
    [Arguments(AgentOrigin.Local, "Failed", true, true)]
    [Arguments(AgentOrigin.Local, "Completed", true, true)]
    [Arguments(AgentOrigin.Local, "Running", false, true)]
    [Arguments(AgentOrigin.Local, "Running", true, false)]
    public async Task A_held_stop_is_cleared_only_by_a_live_local_source(AgentOrigin update, string status, bool connected, bool ended) {
        var (local, remote, sessions, _, directory) = Build("finished");
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        if (!connected) local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        if (update == AgentOrigin.Local) {
            local.Agents.AddOrUpdate(Local(status: status));
            await Assert.That(directory.Rows.Lookup("local:local").HasValue).IsEqualTo(status != "Completed" || !connected);
            local.Agents.RemoveKey("local");
        } else {
            remote.Cache.AddOrUpdate(Remote());
            remote.Cache.RemoveKey("remote");
        }
        await Assert.That(directory.Rows.Lookup("server:S").Value.IsEndedSession).IsEqualTo(ended);
    }

    [Test]
    [Arguments("sess-1", false)]
    [Arguments("sess-1", true)]
    [Arguments("0D1E2F30-4455-6677-8899-AABBCCDDEEFF", false)]
    [Arguments("0D1E2F30-4455-6677-8899-AABBCCDDEEFF", true)]
    public async Task A_stop_before_the_first_listing_keeps_checkout_and_exact_reported_identity(string sent, bool token) {
        var (local, _, sessions, time, directory) = Build("recorded");
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        sessions.LiveCache.Clear();
        sessions.MineLiveCache.Clear();
        var own = SessionIds.Canonical(sent)!;
        var listed = token ? SessionIds.ServerToken(own) : own;
        local.Agents.AddOrUpdate(Local(session: sent));
        directory.NoteFinished(sent);
        local.Agents.RemoveKey("local");
        Publish(sessions, Listed(listed));
        var row = directory.Rows.Lookup("server:" + listed).Value;
        await Assert.That(row.Id).IsEqualTo(listed);
        await Assert.That(row.SessionId).IsEqualTo(listed);
        await Assert.That(row.IsEndedSession).IsTrue();
        await Assert.That(row.WorktreePath).IsEqualTo(InitialWorktree);
        await Assert.That(row.Branch).IsEqualTo("daemon-initial");
        time.Advance(AgentDirectory.FinishedHold);
        await Assert.That(directory.Rows.Lookup("server:" + listed).Value.IsEndedSession).IsFalse();
    }

    /// A down daemon's registry row still stands in for its session, but nothing hosts it, so it opens no handover grace.
    [Test]
    [Arguments("Running", true)]
    [Arguments(AgentRow.DaemonDownStatus, false)]
    public async Task Only_a_live_registry_row_renews_the_hosted_grace(string status, bool renews) {
        var now = DateTime.UnixEpoch;
        var row = AgentRow.FromRemote(Remote()) with { Status = status };

        var (state, kept) = SessionMergeState.Empty.Transition([row], Listed(), localConnected: false, registryCurrent: true, now);

        await Assert.That(kept).Contains(row.Key);
        await Assert.That(state.HostedUntil).IsEqualTo(renews ? now + AgentDirectory.HandoverGrace : null);
    }

    [Test]
    public async Task Alias_matching_prefers_the_canonical_listing_without_renaming_other_server_keys() {
        var (local, _, sessions, _, directory) = Build("recorded");
        using var ownedDirectory = directory;
        using var ownedSessions = sessions;
        sessions.LiveCache.Clear();
        sessions.MineLiveCache.Clear();
        const string own = "sess-1";
        var token = SessionIds.ServerToken(own);
        Publish(sessions, Listed(own));
        Publish(sessions, Listed(token));
        directory.NoteFinished(own);
        local.Agents.AddOrUpdate(Local(session: own));
        await Assert.That(directory.Rows.Lookup("server:" + own).HasValue).IsFalse();
        await Assert.That(directory.Rows.Lookup("server:" + token).Value.IsEndedSession).IsTrue();
        await Assert.That(directory.RowForSession(token)).IsEqualTo(("local", AgentOrigin.Local));
        local.Agents.RemoveKey("local");
        await Assert.That(directory.Rows.Lookup("server:" + own).Value.IsEndedSession).IsFalse();
        await Assert.That(directory.Rows.Lookup("server:" + token).Value.IsEndedSession).IsTrue();
    }
}
