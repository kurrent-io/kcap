using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    // Launch-outcome correlation: the accepted id is request-accepted, not success.

    /// Pushes the failure before returning the launch Task, to exercise the race where a
    /// LaunchFailed arrives while StartAsync's invoke is still in flight.
    sealed class FailureBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(AgentId, Reason));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    public async Task DenialReasonRendersFriendly() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("launch_denied_by_owner: prompt_no_ui"))
            .Contains("consent policy denied");

    [Test]
    public async Task A_full_daemon_reads_as_a_notice_without_the_hub_lead() {
        var text = HomeViewModel.FriendlyLaunchFailure(
            "An unexpected error occurred invoking 'RequestLaunchAgentV2' on the server. HubException: "
            + "Daemon 'sbx-claude-ai_cloud' is full (10 of 10 in use). Stop an agent or wait for one to finish.");

        await Assert.That(text).IsEqualTo(
            "sbx-claude-ai_cloud is already running 10 of 10 agents. Stop one, or wait for one to finish, to start another.");
        await Assert.That(HomeViewModel.IsAtCapacityNotice(text)).IsTrue();
    }

    [Test]
    public async Task Any_hub_refusal_loses_the_transport_lead() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(
                "An unexpected error occurred invoking 'RequestLaunchAgentV2' on the server. HubException: Machine is offline."))
            .IsEqualTo("Machine is offline.");

    [Test]
    public async Task A_cancelled_launch_reads_as_cancelled() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(WireTokens.LaunchCancelled)).IsEqualTo("The launch was cancelled.");

    [Test]
    public async Task An_empty_snapshot_failure_is_a_sentence() {
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("standalone_snapshot_empty"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("git commit -m Initial snapshot failed:"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(
                "git commit -m Initial snapshot failed: On branch main\nnothing to commit"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(
                "git commit -m Initial snapshot failed: fatal: unable to write new index file"))
            .IsEqualTo(HomeViewModel.WorkspacePrepFailed + " Unable to write new index file");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_repository_clears_the_start_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";
            launch.Next = new LaunchOutcome(false, null, "git commit -m Initial snapshot failed:");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);

            // Same folder under a different casing is still that folder on macOS/Windows.
            if (!OperatingSystem.IsLinux()) {
                await vm.SelectRepositoryAsync("/repo/A");
                await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
            }

            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_the_previous_repository_does_not_stick() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await vm.SelectRepositoryAsync("/repo/b");
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_the_previous_machine_does_not_stick() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/repo/a"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await vm.SelectMachineAsync("home-pc", isLocal: false);
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_local_does_not_stick_on_a_same_named_remote() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService { DaemonName = "home-pc" };
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/repo/a"],
                    MachineId = "remote-machine",
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                localMachineId: "local-machine");

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await vm.SelectMachineAsync("home-pc", isLocal: false);
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.SelectedMachine).IsEqualTo("home-pc");
            await Assert.That(vm.RemoteMachineSelected).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchFailureAfterAcceptSetsStartError() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task FailureBeforeInvokeReturnsIsStillApplied() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            var launch = new FailureBeforeReturnLaunchClient {
                Failures = failures, AgentId = "agent-9", Reason = "launch_denied_by_owner: default",
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ForeignFailuresAreIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("other-id", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RowAppearanceClearsPendingSoLateFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent("agent-9", "/repo/a"));
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// The hub can return either id shape (NormalizeAgentId's own comment) — a failure carrying
    /// the DASHED form must still correlate against the "N"-normalized key StartAsync recorded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidFailureCorrelatesWithTheNormalizedPendingKey() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// Same id-shape mismatch as above, on the row-confirmation path: a directory row carrying
    /// the dashed form must still clear the "N"-normalized pending entry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidRowClearsThePendingEntrySoALateDashedFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent(dashed, "/repo/a"));
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Adds the directory row before the launch call returns, so it exists before RecordPendingLaunch
    /// ever runs — the race a directory-row Add event can never observe (nothing was pending yet).
    sealed class RowBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required FakeDaemonClientService Daemon { get; init; }
        public required string AgentId { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Daemon.Agents.AddOrUpdate(Agent(AgentId, request.RepoPath));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = "agent-9" };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Same predates-recording race as above, but with a DASHED-Guid row and a dashed accepted
    /// id — RowExists must compare under NormalizeAgentId, not a raw "local:{id}" key lookup,
    /// since the directory key preserves the row's incoming id spelling verbatim.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ADashedGuidRowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = dashed };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Adds the directory row AND pushes a LaunchFailure for the same id before StartAsync's own
    /// invoke returns — RecordPendingLaunch must confirm the row and discard the buffered failure
    /// without ever rendering it, whichever order its own two checks run in.
    sealed class RowAndFailureBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required FakeDaemonClientService Daemon { get; init; }
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Daemon.Agents.AddOrUpdate(Agent(AgentId, request.RepoPath));
            Failures.OnNext(new LaunchFailure(AgentId, Reason));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowPresentAndABufferedFailureBeforeReturnNeverRendersTheFailure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            var launch = new RowAndFailureBeforeReturnLaunchClient {
                Daemon = daemon, Failures = failures, AgentId = "agent-9",
                Reason = "launch_denied_by_owner: default",
            };
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// A directory that reports every read of its Rows, so a test can land an Add at a chosen point
    /// of the caller's own read sequence — the only way to reach the window between HomeViewModel's
    /// two row checks, which the Add event itself cannot close (nothing is pending when it fires).
    sealed class ReadHookedDirectory : IAgentDirectory, IDisposable {
        readonly SourceCache<AgentRow, string> _source = new(r => r.Key);
        readonly IObservableCache<AgentRow, string> _rows;
        public Action? OnRead;

        public ReadHookedDirectory() => _rows = _source.AsObservableCache();

        public IObservableCache<AgentRow, string> Rows {
            get { OnRead?.Invoke(); return _rows; }
        }

        public IObservable<bool> RemoteStale => Observable.Return(false);
        public IObservable<bool> LocalDaemonOnAppServer => Observable.Return(true);

        public IObservable<IReadOnlyDictionary<string, string>> SessionAgents =>
            Observable.Return((IReadOnlyDictionary<string, string>)FrozenDictionary<string, string>.Empty);
        public string? VendorOfSession(string sessionId) => null;
        public (string Id, AgentOrigin Origin)? RowForSession(string sessionId) => null;
        public bool IsProvenLocalTwin(string agentId) => false;
        public bool IsHostedNow(AgentRow row) => false;
        public void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model) { }
        public void AddRemotePlaceholder(
            string agentId, string machine, string? ownerUserId, string vendor, string repoPath, string? title, string? model) { }
        public void RemovePlaceholder(string agentId) { }

        public void Add(string agentId) => _source.AddOrUpdate(
            AgentRow.FromLocal(Agent(agentId, "/repo/a"), new RepoIdentity("path:/repo/a", "repo")));

        public void Dispose() {
            _rows.Dispose();
            _source.Dispose();
        }
    }

    /// Buffers a failure for the launched id, then arms the directory to add that id's row on the
    /// SECOND row check of the launch that follows — the first is RecordPendingLaunch's leading
    /// check, the second its re-check after registering the pending entry.
    sealed class RowBetweenTheRowChecksLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required ReadHookedDirectory Directory { get; init; }
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(AgentId, "launch_denied_by_owner: default"));
            var reads = 0;
            Directory.OnRead = () => { if (++reads == 2) Directory.Add(AgentId); };
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowAppearingAfterTheLeadingCheckStillDiscardsTheBufferedFailure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            using var directory = new ReadHookedDirectory();
            var launch = new RowBetweenTheRowChecksLaunchClient {
                Directory = directory, Failures = failures, AgentId = "agent-9",
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
        });
    }
}
