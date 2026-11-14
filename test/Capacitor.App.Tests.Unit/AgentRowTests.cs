using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class AgentRowTests {
    static RepoSessionDto Listed(
            string id = "s1", string status = "ended", string? repoOwner = "kurrent-io", string? repoName = "kcap-cli",
            string? cwd = "/Users/alexey/dev/kcap-cli", bool stale = false) => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = "Desktop app RAM growth",
        Owner = new() { UserId = "user_01", Username = "alexey", DisplayName = "Alexey Zimarev", AvatarUrl = "https://avatars.example/user_01" },
        StartedAt = new DateTimeOffset(2026, 10, 22, 13, 4, 24, TimeSpan.Zero),
        EndedAt = status == "ended" ? new DateTimeOffset(2026, 10, 22, 16, 56, 41, TimeSpan.Zero) : null,
        LastActivityAt = new DateTimeOffset(2026, 10, 22, 16, 56, 41, TimeSpan.Zero),
        Cwd = cwd, Stale = stale,
        Repo = repoOwner is null ? null : new() { Hash = "830e1690b97dd8c0", Owner = repoOwner, Name = repoName },
    };

    [Test]
    public async Task Server_row_carries_the_recording_identity() {
        var recording = new RepoSessionRecordingDto { MachineId = "m-1", DaemonName = "laptop", AccountId = "acct-1", ConfigDir = "/Users/me/.claude" };

        var row = AgentRow.FromServer(Listed() with { Recording = recording }, hostedPresentation: false);

        await Assert.That(row.Recording).IsEqualTo(recording);
        await Assert.That(row.RecordedOn("m-1")).IsTrue();
        await Assert.That(row.RecordedOn("m-2")).IsFalse();
    }

    /// A listing from an older server, or for a session recorded before its CLI reported it, names no
    /// machine; neither it nor an app with no machine id of its own may read as this machine.
    [Test]
    public async Task A_missing_machine_id_is_never_this_machine() {
        var unrecorded = AgentRow.FromServer(Listed(), hostedPresentation: false);
        var noMachine  = AgentRow.FromServer(Listed() with { Recording = new() { ConfigDir = "/Users/me/.claude" } }, hostedPresentation: false);
        var recorded   = AgentRow.FromServer(Listed() with { Recording = new() { MachineId = "m-1" } }, hostedPresentation: false);

        await Assert.That(unrecorded.RecordedOn("m-1")).IsFalse();
        await Assert.That(noMachine.RecordedOn("m-1")).IsFalse();
        await Assert.That(recorded.RecordedOn(null)).IsFalse();
        await Assert.That(recorded.RecordedOn("")).IsFalse();
    }

    [Test]
    public async Task Server_row_maps_an_ended_listing_row() {
        var row = AgentRow.FromServer(Listed(), hostedPresentation: false);

        await Assert.That(row.Key).IsEqualTo("server:s1");
        await Assert.That(row.Origin).IsEqualTo(AgentOrigin.Server);
        await Assert.That(row.Id).IsEqualTo("s1");
        await Assert.That(row.SessionId).IsEqualTo("s1");
        await Assert.That(row.Status).IsEqualTo("Completed");
        await Assert.That(row.Vendor).IsEqualTo("claude");
        await Assert.That(row.RepoGroupKey).IsEqualTo("repo:kurrent-io/kcap-cli");
        await Assert.That(row.RepoGroupLabel).IsEqualTo("kurrent-io/kcap-cli");
        await Assert.That(row.OwnerUserId).IsEqualTo("user_01");
        await Assert.That(row.OwnerName).IsEqualTo("Alexey Zimarev");
        await Assert.That(row.OwnerAvatarUrl).IsEqualTo("https://avatars.example/user_01");
        await Assert.That(row.CreatedAt).IsEqualTo(new DateTime(2026, 10, 22, 13, 4, 24, DateTimeKind.Utc));
        await Assert.That(row.EndedAt).IsEqualTo(new DateTime(2026, 10, 22, 16, 56, 41, DateTimeKind.Utc));
        await Assert.That(row.LastActivityAt).IsEqualTo(new DateTime(2026, 10, 22, 16, 56, 41, DateTimeKind.Utc));
        await Assert.That(row.IsEndedSession).IsTrue();
        await Assert.That(row.IsTerminalSession).IsFalse();
        await Assert.That(row.MachineBadge).IsNull();
    }

    [Test]
    public async Task Server_row_without_repo_or_cwd_groups_under_no_repository() {
        var row = AgentRow.FromServer(Listed(repoOwner: null, cwd: null), hostedPresentation: false);

        await Assert.That(row.RepoGroupLabel).IsEqualTo("No repository");
        await Assert.That(row.RepoGroupKey).IsEqualTo("daemon:server:");
        await Assert.That(row.RepoPath).IsNull();
    }

    /// After an app restart nothing but the listing describes the session: a hosted agent's cwd is
    /// its worktree, and the checkout that owns it is the repository.
    [Test]
    [Arguments("/Users/u/dev/shop-api/.capacitor/worktrees/agent-1a2b3c4d5e6f", "/Users/u/dev/shop-api")]
    [Arguments(@"C:\src\shop-api\.claude\worktrees\snappy-leaf", @"C:\src\shop-api")]
    public async Task Server_row_whose_cwd_is_an_agent_worktree_names_the_owning_checkout(string cwd, string repo) {
        var row = AgentRow.FromServer(Listed(cwd: cwd), hostedPresentation: false);

        await Assert.That(row.RepoPath).IsEqualTo(repo);
        await Assert.That(row.WorktreePath).IsEqualTo(cwd);
    }

    /// A session started in a directory below the worktree still names the worktree, not that directory.
    [Test]
    public async Task Server_row_whose_cwd_is_inside_an_agent_worktree_names_the_worktree_root() {
        var row = AgentRow.FromServer(Listed(cwd: "/u/shop-api/.capacitor/worktrees/agent-1/src/api"), hostedPresentation: false);

        await Assert.That(row.RepoPath).IsEqualTo("/u/shop-api");
        await Assert.That(row.WorktreePath).IsEqualTo("/u/shop-api/.capacitor/worktrees/agent-1");
    }

    /// An agent worktree inside another's belongs to the innermost one, whichever marker each uses.
    [Test]
    public async Task Server_row_whose_cwd_nests_agent_worktrees_names_the_innermost() {
        var row = AgentRow.FromServer(Listed(cwd: "/u/shop-api/.capacitor/worktrees/agent-1/.claude/worktrees/leaf/src"), hostedPresentation: false);

        await Assert.That(row.RepoPath).IsEqualTo("/u/shop-api/.capacitor/worktrees/agent-1");
        await Assert.That(row.WorktreePath).IsEqualTo("/u/shop-api/.capacitor/worktrees/agent-1/.claude/worktrees/leaf");
    }

    /// Without a repository label the rail groups by path; two agents of one checkout share its group.
    [Test]
    public async Task Server_rows_without_a_label_group_agent_worktrees_under_their_checkout() {
        var first = AgentRow.FromServer(Listed(id: "s1", repoOwner: null, cwd: "/u/shop-api/.capacitor/worktrees/agent-1"), hostedPresentation: false);
        var second = AgentRow.FromServer(Listed(id: "s2", repoOwner: null, cwd: "/u/shop-api/.capacitor/worktrees/agent-2"), hostedPresentation: false);

        await Assert.That(first.RepoGroupKey).IsEqualTo(second.RepoGroupKey);
        await Assert.That(first.RepoGroupLabel).IsEqualTo("shop-api");
    }

    /// Any other cwd may be a linked worktree too; naming it as one would call it the main checkout.
    [Test]
    public async Task Server_row_whose_cwd_is_no_agent_worktree_names_no_worktree() {
        var row = AgentRow.FromServer(Listed(cwd: "/Users/u/worktrees/shop-api/fix-login"), hostedPresentation: false);

        await Assert.That(row.RepoPath).IsEqualTo("/Users/u/worktrees/shop-api/fix-login");
        await Assert.That(row.WorktreePath).IsNull();
    }

    [Test]
    public async Task Live_server_row_is_a_terminal_session_unless_hosted() {
        var terminal = AgentRow.FromServer(Listed(status: "active", stale: true), hostedPresentation: false);
        var hosted = AgentRow.FromServer(Listed(status: "active"), hostedPresentation: true);

        await Assert.That(terminal.Status).IsEqualTo("Running");
        await Assert.That(terminal.IsTerminalSession).IsTrue();
        await Assert.That(terminal.IsStale).IsTrue();
        await Assert.That(terminal.IsEndedSession).IsFalse();
        await Assert.That(terminal.EndedAt).IsNull();
        await Assert.That(hosted.IsTerminalSession).IsFalse();
    }

    [Test]
    public async Task Remote_row_carries_the_registry_owner() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "a1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "user_02", Vendor = "codex",
        });

        await Assert.That(row.OwnerUserId).IsEqualTo("user_02");
        await Assert.That(row.IsEndedSession).IsFalse();
    }

    static AgentInstanceDto Remote(string? title, string? prompt = "fix the flaky test\nmore") => new() {
        AgentId = "r1", SessionId = "s2", Status = "Running", DaemonName = "d",
        OwnerUserId = "u7", RegisteredAt = DateTime.UtcNow, Title = title, Prompt = prompt,
    };

    [Test]
    public async Task FromRemote_prefers_the_server_title() {
        var row = AgentRow.FromRemote(Remote("Renamed on the server"));
        await Assert.That(row.Title).IsEqualTo("Renamed on the server");
    }

    [Test]
    public async Task FromRemote_falls_back_to_the_prompt_cut() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).Title).IsEqualTo("fix the flaky test");
        await Assert.That(AgentRow.FromRemote(Remote("")).Title).IsEqualTo("fix the flaky test");
    }

    [Test]
    public async Task FromRemote_carries_the_owner() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).OwnerUserId).IsEqualTo("u7");
    }

    [Test]
    public async Task FromRemote_carries_the_viewer_rights_and_none_from_a_server_without_them() {
        var rights = new AgentViewerRightsDto { Reaches = true, HoldMessages = true };
        await Assert.That(AgentRow.FromRemote(Remote(null) with { ViewerRights = rights }).ViewerRights).IsEqualTo(rights);
        await Assert.That(AgentRow.FromRemote(Remote(null)).ViewerRights).IsNull();
    }

    /// The remote daemon fetches with its owner's credential, so files are offered only when the
    /// signed-in account owns both the session and the daemon, and the daemon's build fetches on
    /// dispatched input. The listing's own ownership flag plays no part: it can be unset, or stale
    /// after an account switch.
    [Test]
    [Arguments("u7",  "u7",    "1.0.3",        null,  true)]
    [Arguments("u7",  "u7",    "1.0.3-beta.1", false, true)]
    [Arguments("u7",  "u7",    "1.0.2",        true,  false)]
    [Arguments("u7",  "other", "1.0.3",        true,  false)]
    [Arguments("u8",  "u7",    "1.0.3",        true,  false)]
    [Arguments(null,  "u7",    "1.0.3",        true,  false)]
    public async Task A_remote_session_accepts_attachments_only_on_the_signed_in_accounts_capable_daemon(
            string? viewer, string daemonOwner, string version, bool? ownerIsViewer, bool expected) {
        var row = AgentRow.FromRemote(Remote("t")) with { OwnerIsViewer = ownerIsViewer };
        IReadOnlyList<DaemonInfo> daemons = [new DaemonInfo { Name = "d", OwnerUserId = daemonOwner, Version = version }];

        await Assert.That(RemoteSessionViewModel.AcceptsAttachments(row, daemons, viewer)).IsEqualTo(expected);
    }

    /// An attached terminal session submits text alone, so even the viewer's own capable daemon refuses files for it.
    [Test]
    public async Task An_attached_session_never_accepts_attachments() {
        var row = AgentRow.FromRemote(Remote("t")) with { OwnerIsViewer = true, Kind = AgentActionService.AttachedKind };
        IReadOnlyList<DaemonInfo> daemons = [new DaemonInfo { Name = "d", OwnerUserId = "u7", Version = "1.0.3" }];

        await Assert.That(row.IsAttached).IsTrue();
        await Assert.That(RemoteSessionViewModel.AcceptsAttachments(row, daemons, "u7")).IsFalse();
    }
}
