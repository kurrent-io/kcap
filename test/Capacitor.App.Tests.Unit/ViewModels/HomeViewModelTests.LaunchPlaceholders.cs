using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    static AgentRow RemoteRow(string agentId) => AgentRow.FromRemote(new AgentInstanceDto {
        AgentId = agentId, Status = "Running", DaemonName = "home-pc", OwnerUserId = "u1",
        Vendor = "claude", RepoOwner = "o", RepoName = "r",
    });

    /// Like the window's own, an open under a stale generation is refused and one that lands bumps it.
    static HomeViewModel RemoteLauncher(
            string path, FakeAgentDirectory directory, List<(string AgentId, AgentOrigin Origin, int Generation)> opened,
            RecordingLaunchClient? launch = null, StrongBox<int>? navigationBox = null, Action<string>? afterOpen = null) {
        var navigation = navigationBox ?? new StrongBox<int>(7);
        var remote = new FakeRemoteAgents();
        remote.DaemonsSubject.OnNext([
            new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
        ]);
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        return new HomeViewModel(
            new FakeDaemonClientService(), new AppStateStore(path), launch ?? new RecordingLaunchClient(), Known(), TimeProvider.System,
            navigationGeneration: () => navigation.Value,
            openSessionIfCurrent: (id, origin, generation) => {
                if (generation != navigation.Value) return;
                opened.Add((id, origin, generation));
                navigation.Value++;
                afterOpen?.Invoke(id);
            },
            daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status,
            directory: directory);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchOpensOnItsPlaceholderAtOnce() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(directory.RemotePlaceholders).IsEquivalentTo([(LaunchedId, "home-pc", (string?)"u1", vm.SelectedVendor, "/w/repo")]);
            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7)]);
        });
    }

    /// The placeholder row in the directory does not settle the launch; the registry's row does,
    /// and it reaches the open placeholder workspace under the generation that open left behind.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchSettlesOnTheRegistryRowNotItsPlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.RemotePlaceholder(
                LaunchedId, "home-pc", "claude", "/w/repo", null, null, DateTime.UtcNow, new RepoIdentity("repo:o/r", "o/r"), "u1/home-pc"));
            await Assert.That(opened).Count().IsEqualTo(1);

            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    /// A navigation while the launch was in flight refuses the placeholder's open, and the registry
    /// row arriving later must not open over what the user moved to either.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchTheUserNavigatedAwayFromNeverOpens() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var navigation = new StrongBox<int>(7);
            var launch = new RecordingLaunchClient { During = () => navigation.Value++ };
            using var vm = RemoteLauncher(path, directory, opened, launch, navigation);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNotNull();
            await Assert.That(navigation.Value).IsEqualTo(8);
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEmpty();
        });
    }

    /// Holds what is scheduled on it until drained, as the UI thread holds a posted open until the
    /// work it is doing returns.
    sealed class QueuedScheduler : IScheduler {
        readonly Queue<Action> _queue = new();
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) {
            _queue.Enqueue(() => action(this, state));
            return System.Reactive.Disposables.Disposable.Empty;
        }
        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);
        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);
        public void Drain() { while (_queue.TryDequeue(out var next)) next(); }
    }

    /// The registry's differently spelled row can be confirmed off the UI thread while the placeholder
    /// is still opening; its open runs after that, and must carry the generation the open left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARegistryRowConfirmedWhileThePlaceholderOpensStillTakesOver() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var dashed = Guid.ParseExact(LaunchedId, "N").ToString("D");
            var ui = new QueuedScheduler();
            using var vm = RemoteLauncher(path, directory, opened, new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) },
                afterOpen: id => {
                    if (id != dashed) return;
                    var prior = RxSchedulers.MainThreadScheduler;
                    RxSchedulers.MainThreadScheduler = ui;
                    try { directory.Rows.AddOrUpdate(RemoteRow(LaunchedId)); } finally { RxSchedulers.MainThreadScheduler = prior; }
                });
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            ui.Drain();

            await Assert.That(opened).IsEquivalentTo([(dashed, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    /// The registry may spell the id another way than the launch's accept did; its row still takes
    /// over from the placeholder workspace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARegistryRowSpelledDifferentlyStillOpensFromThePlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var dashed = Guid.ParseExact(LaunchedId, "N").ToString("D");
            using var vm = RemoteLauncher(path, directory, opened, new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) });
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(dashed, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteRowThatBeatTheLaunchReturnOpensAtOnce() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7)]);
        });
    }

    /// A same-id local row is a different agent: only the remote lane's row settles a remote launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalRowWithTheSameIdDoesNotSettleARemoteLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent(LaunchedId, "claude", false, "/w/repo"), new RepoIdentity("path:/w/repo", "repo")));
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalLaunchStillAutoOpensTheWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                openSessionIfCurrent: (id, origin, generation) => opened.Add((id, origin, generation)));
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(opened.Count).IsEqualTo(1);
            await Assert.That(opened[0].AgentId).IsEqualTo(LaunchedId);
            await Assert.That(opened[0].Origin).IsEqualTo(AgentOrigin.Local);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalLaunchAddsAPlaceholderRowForTheAcceptedId() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "Fix the flaky test\nand more";
            await vm.StartCommand.Execute();

            await Assert.That(directory.Placeholders).Count().IsEqualTo(1);
            var placeholder = directory.Placeholders[0];
            await Assert.That(placeholder.Id).IsEqualTo(LaunchedId);
            await Assert.That(placeholder.Vendor).IsEqualTo(vm.SelectedVendor);
            await Assert.That(placeholder.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(placeholder.Title).IsEqualTo("Fix the flaky test");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchAddsNoLocalPlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status, directory: directory);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            await Assert.That(directory.Placeholders).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALaunchFailureRemovesThePlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "boom"));

            await Assert.That(directory.RemovedPlaceholders).Contains("agent-9");
        });
    }

    /// Same-id rows on different lanes are different agents, so a remote row can neither stand in
    /// for a local launch's placeholder nor settle its failure tracking.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteRowWithTheSameIdDoesNotSettleALocalLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "agent-9", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RepoOwner = "o", RepoName = "r",
            }));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await Assert.That(directory.Placeholders.Select(p => p.Id)).Contains("agent-9");

            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// The pending row is the launch's own stand-in, so its arrival proves nothing: a failure
    /// after it must still render, and only a real agent row settles the launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task APendingRowDoesNotConfirmTheLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.FromPending(
                new PendingLaunchDto("agent-9", "claude", "/repo/a", null, DateTime.UtcNow, "spawned"), new RepoIdentity("path:/repo/a", "a")));
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }
}
