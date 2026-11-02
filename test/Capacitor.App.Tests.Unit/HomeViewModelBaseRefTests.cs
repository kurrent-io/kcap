using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// The launcher's "Branch from" choice: what it accepts, what it sends, when it is cleared, and how
/// the per-repository list of recent refs follows launches that started or could not find the ref.
/// Runs under AvaloniaSession for the same reason as HomeViewModelTests.
public class HomeViewModelBaseRefTests {
    const string AgentId = "0123456789abcdef0123456789abcdef";

    sealed class LaunchClient : ILaunchClient {
        public IReadOnlySet<string>? Features = new HashSet<string> { HomeViewModel.BaseRefLaunchFeature };
        public LaunchRequest? Last;
        /// Publishes the agent's row before the call returns, as a launch that started at once does.
        public FakeDaemonClientService? RowOn;

        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) => Task.FromResult(Features);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            RowOn?.Agents.AddOrUpdate(new AgentStatusDto(
                AgentId, "agent", "claude", request.RepoPath, "Running",
                FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
                RequesterDisplay: null));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    static FakeDaemonClientService ConnectedDaemon() {
        var daemon = new FakeDaemonClientService();
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        return daemon;
    }

    static Func<Task<string[]>> NoKnownRepos() => () => Task.FromResult(Array.Empty<string>());

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_ref_rides_the_launch_and_blank_means_head() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var launch = new LaunchClient();
            using var vm = new HomeViewModel(ConnectedDaemon(), new AppStateStore(path), launch, NoKnownRepos(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.ChooseBaseRef("  feature/x ")).IsNull();
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.BaseRef).IsEqualTo("feature/x");

            await Assert.That(vm.ChooseBaseRef("   ")).IsNull();
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.BaseRef).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_malformed_ref_is_refused_and_the_selection_kept() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var vm = new HomeViewModel(ConnectedDaemon(), new AppStateStore(path), new LaunchClient(), NoKnownRepos(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.ChooseBaseRef("main");

            await Assert.That(vm.ChooseBaseRef("bad name")).IsNotNull();
            await Assert.That(vm.SelectedBaseRef).IsEqualTo("main");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Picking_another_repository_returns_to_head() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var vm = new HomeViewModel(ConnectedDaemon(), new AppStateStore(path), new LaunchClient(), NoKnownRepos(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.ChooseBaseRef("main");

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedBaseRef).IsEqualTo("main");

            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.SelectedBaseRef).IsNull();
        });
    }

    /// A server that does not forward the field would start the session from HEAD while the chip says otherwise.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_server_without_the_feature_refuses_before_launching() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var launch = new LaunchClient { Features = FrozenSet<string>.Empty };
            using var vm = new HomeViewModel(ConnectedDaemon(), new AppStateStore(path), launch, NoKnownRepos(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.ChooseBaseRef("main");

            await vm.StartCommand.Execute();

            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.BaseRefUnsupportedByServer);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_started_session_remembers_its_ref_for_that_repository_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = ConnectedDaemon();
            var launch = new LaunchClient { RowOn = daemon };
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, NoKnownRepos(), TimeProvider.System, directory: directory);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.ChooseBaseRef("feature/x");

            await vm.StartCommand.Execute();

            await Assert.That(await vm.ListRecentBaseRefsAsync()).IsEquivalentTo(new[] { "feature/x" });
            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(await vm.ListRecentBaseRefsAsync()).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_launch_that_cannot_find_its_ref_drops_it_from_the_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var store = new AppStateStore(path);
            await store.UpdateAsync(s => s with {
                BaseRefsByRepo = BaseRefMemory.Remember(null, "local", "/repo/a", StringComparer.Ordinal, "gone"),
            });
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                ConnectedDaemon(), store, new LaunchClient(), NoKnownRepos(), TimeProvider.System, launchFailures: failures);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.ChooseBaseRef("gone");

            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(AgentId,
                "git fetch origin gone:refs/kcap/fetched/agent-1 failed: fatal: couldn't find remote ref gone"));

            await Assert.That(vm.StartError).IsEqualTo("There's no branch, tag or commit “gone” on that machine or its origin.");
            await Assert.That(await vm.ListRecentBaseRefsAsync()).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Forgetting_a_ref_removes_it_from_the_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var store = new AppStateStore(path);
            var refs = BaseRefMemory.Remember(null, "local", "/repo/a", StringComparer.Ordinal, "one");
            refs = BaseRefMemory.Remember(refs, "local", "/repo/a", StringComparer.Ordinal, "two");
            await store.UpdateAsync(s => s with { BaseRefsByRepo = refs });
            using var vm = new HomeViewModel(ConnectedDaemon(), store, new LaunchClient(), NoKnownRepos(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");

            await vm.ForgetBaseRefAsync("two");

            await Assert.That(await vm.ListRecentBaseRefsAsync()).IsEquivalentTo(new[] { "one" });
        });
    }

    [Test]
    [Arguments("invalid_base_ref: a..b", "“a..b” isn't a branch, tag or commit name git accepts.")]
    [Arguments("base_ref_unsupported: daemon 'box' cannot start a session from a chosen branch.",
        "Daemon 'box' cannot start a session from a chosen branch.")]
    [Arguments("git fetch origin 0123abcd:refs/kcap/fetched/x failed: fatal: remote error: upload-pack: not our ref 0123abcd",
        "There's no branch, tag or commit “0123abcd” on that machine or its origin.")]
    public async Task Base_ref_failures_read_as_sentences(string reason, string expected) {
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(reason)).IsEqualTo(expected);
    }

    [Test]
    public async Task Only_a_ref_that_resolves_nowhere_counts_as_missing() {
        await Assert.That(HomeViewModel.IsMissingBaseRef("invalid_base_ref: x")).IsTrue();
        await Assert.That(HomeViewModel.IsMissingBaseRef("git fetch origin x:y failed: fatal: couldn't find remote ref x")).IsTrue();
        await Assert.That(HomeViewModel.IsMissingBaseRef("git fetch origin x:y failed: fatal: unable to access")).IsFalse();
        await Assert.That(HomeViewModel.IsMissingBaseRef("launch_denied_by_owner: default")).IsFalse();
    }
}
