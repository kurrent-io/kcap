using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    static AgentRow LocalRow(string repoPath) =>
        AgentRow.FromLocal(WorkspaceFixtures.Agent("a1", "claude", false, repoPath), new RepoIdentity("path:" + repoPath, "repo"));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_session_in_a_worktree_selects_its_main_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string main = Tmp.CreateDir("main");
            string gitDir = Tmp.CreateDir("main", ".git", "worktrees", "wt");
            string worktree = Tmp.CreateDir("wt");
            Tmp.CreateFile(["wt", ".git"], $"gitdir: {gitDir}\n");
            var vm = Build(out _, out _, Tmp.PathTo("app-state.json"));

            await vm.SelectRepositoryOfAsync(LocalRow(worktree));

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(PlatformPaths.Normalize(main));
        });
    }

    /// A server row's path names a directory on whichever machine ran it; one that is not here is not a pick.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_whose_path_is_not_on_this_machine_keeps_the_selection() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = Build(out _, out _, Tmp.PathTo("app-state.json"));
            await vm.SelectRepositoryAsync("/repo/a");

            await vm.SelectRepositoryOfAsync(LocalRow(Tmp.PathTo("missing")) with { Origin = AgentOrigin.Server });

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
        });
    }

    [Test]
    [Arguments("u1", "u1", true)]
    [Arguments("someone-else", "someone-else", false)]
    [Arguments("u1", "someone-else", false)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_selects_its_machine_and_repository_only_when_the_viewer_owns_the_session_and_machine(
            string owner, string rowOwner, bool adopted) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box(owner)]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectRepositoryAsync("/repo/a");

            await vm.SelectRepositoryOfAsync(LocalRow("/srv/repo") with { Origin = AgentOrigin.Remote, MachineBadge = "box", OwnerUserId = rowOwner });

            await Assert.That(vm.RemoteMachineSelected).IsEqualTo(adopted);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(adopted ? "/srv/repo" : "/repo/a");
        });
    }

    [Test]
    [Arguments("/srv/repo/.capacitor/worktrees/agent-1")]
    [Arguments("/srv/repo/.claude/worktrees/agent-1")]
    [Arguments("/srv/repo/.capacitor/worktrees/agent-1/.claude/worktrees/agent-2")]
    [Arguments("/srv/repo/.capacitor/worktrees/agent-1/src")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_in_an_agent_worktree_selects_the_repository_that_owns_it(string worktree) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectRepositoryOfAsync(LocalRow(worktree) with { Origin = AgentOrigin.Remote, MachineBadge = "box", OwnerUserId = "u1" });

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/srv/repo");
        });
    }

    /// A repository checked out in a subdirectory of an agent worktree owns the worktrees under it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_in_a_repository_nested_in_an_agent_worktree_keeps_that_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectRepositoryOfAsync(LocalRow("/srv/repo/.capacitor/worktrees/agent-1/vendor/lib/.claude/worktrees/agent-2") with {
                Origin = AgentOrigin.Remote, MachineBadge = "box", OwnerUserId = "u1" });

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/srv/repo/.capacitor/worktrees/agent-1/vendor/lib");
        });
    }

    sealed class GatedAppStateStore : IAppStateStore {
        public AppState State = new();
        public TaskCompletionSource<AppState>? Hold;

        public Task<AppState> LoadAsync() => Hold?.Task ?? Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            State = mutate(State);
            return Task.FromResult(true);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_permission_mode_picked_while_the_repository_loads_is_kept() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var store = new GatedAppStateStore();
            store.State = store.State with {
                PermissionModeByRepo = new Dictionary<string, string> { ["/repo/a"] = ClaudePermissionModes.BypassPermissions } };
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, store, new RecordingLaunchClient(), Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/b");

            var loaded = store.State;
            store.Hold = new TaskCompletionSource<AppState>();
            var restore = vm.SelectRepositoryAsync("/repo/a");
            await vm.ChoosePermissionModeAsync(ClaudePermissionModes.AcceptEdits);
            store.Hold.SetResult(loaded);
            await restore;

            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(ClaudePermissionModes.AcceptEdits);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repository_picked_while_a_session_is_resolved_wins() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1")]);
            var viewer = new TaskCompletionSource<string?>();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => viewer.Task);
            await vm.SelectRepositoryAsync("/repo/a");

            var follow = vm.SelectRepositoryOfAsync(LocalRow("/srv/repo") with {
                Origin = AgentOrigin.Remote, MachineBadge = "box", OwnerUserId = "u1" });
            await vm.SelectRepositoryAsync("/repo/b");
            viewer.SetResult("u1");
            await follow;

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/b");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_viewer_read_leaves_the_selection_alone() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, viewerId: _ => throw new IOException("claims unreadable"));
            await vm.SelectRepositoryAsync("/repo/a");

            await vm.SelectRepositoryOfAsync(LocalRow("/srv/repo") with {
                Origin = AgentOrigin.Remote, MachineBadge = "box", OwnerUserId = "u1" });

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
        });
    }
}
