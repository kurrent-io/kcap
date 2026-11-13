using TUnit.Assertions.Enums;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    // The launcher's machine picker: name-based launch routing is only defined within one owner.
    // ListMachinesAsync applies that filter at listing time; every test below reads its output
    // rather than trusting a wired-through daemon list. StartAsync separately re-verifies
    // ownership at invocation time — see the launch-path tests further down for that layer.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerOffersLocalPlusOwnConnectedDaemons() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", MachineId = "m2", Connected = true },
                new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", MachineId = "m3", Connected = true },
                // Same machine id + name as the local daemon: the local daemon's own registry
                // twin, never a distinct remote target.
                new DaemonInfo { Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m1", Connected = true },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), localMachineId: "m1");

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(2);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
            await Assert.That(options[0].IsLocal).IsTrue();
            await Assert.That(options[0].Selected).IsTrue();
            await Assert.That(options[1].DaemonName).IsEqualTo("home-pc");
            await Assert.That(options.Any(o => o.DaemonName == "work-mac")).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NullViewerIdOffersOnlyLocal() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            // No viewerId supplied — the ctor default never guesses ownership.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System, daemons: remote.Daemons);

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(1);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
        });
    }

    /// The local daemon (server B) and an owned remote daemon (server A) can share a name — name-
    /// based routing alone can never tell them apart, so the caller's own isLocal (from the
    /// clicked MachineOption) is what SelectMachineAsync must trust.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingARemoteEntryThatSharesTheLocalDaemonsNameSelectsTheRemoteOne() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService { DaemonName = "work" };
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "work", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/a/repo"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("work", isLocal: false);

            await Assert.That(vm.RemoteMachineSelected).IsTrue();
            await Assert.That(vm.SelectedMachine).IsEqualTo("work");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/a/repo");
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "codex").Available).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingRemoteMachineSwitchesRepoAndVendorSources() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var store = new AppStateStore(path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo", "/w/repo2"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Count).IsEqualTo(2);
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "codex").Available).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsFalse();
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            // Picking another of the MACHINE's own repo paths must revalidate against that
            // machine's supported vendors — never fall through to the local HarnessByRepo/
            // DefaultVendor rule, which would reset onto "claude" (unsupported here) since
            // "/w/repo2" has no local entry.
            await vm.SelectRepositoryAsync("/w/repo2");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo2");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo is null || saved.HarnessByRepo.Count == 0).IsTrue();
        });
    }

    /// <summary>A machine advertises its repositories most recently used first. The picker lists them by
    /// name like the local list, and the most recently used one stays the selection. A Windows machine's
    /// path is named by its last segment on any desktop.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteMachineRepositoriesAreListedByName() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/widgets", @"Z:\x\Engine", "/w/api"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(),
                TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath).ToArray())
                .IsEquivalentTo(["/w/api", @"Z:\x\Engine", "/w/widgets"], CollectionOrdering.Matching);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/widgets");
        });
    }

    /// Pins the remote repository menu to the registry as it stands at menu open: a path the machine
    /// advertises after it was selected (its first launch there) is offered without re-selecting it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRepositoryMenuReadsTheMachinesCurrentAdvertisement() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo", "/w/new"] },
            ]);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/w/repo", "/w/new"]);
        });
    }

    /// Pins the typed remote path: a full path on any platform is selected, offered in the menu
    /// before the machine advertises it, and never written to the local store; anything else is
    /// refused with the selection left alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task EnteringARemoteRepositorySelectsAFullPathAndRefusesARelativeOne() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var store = new AppStateStore(path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), store, new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(await vm.EnterRemoteRepositoryAsync("git/other")).IsNotNull();
            await Assert.That(await vm.EnterRemoteRepositoryAsync("  ")).IsNotNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");

            await Assert.That(await vm.EnterRemoteRepositoryAsync("  /srv/other  ")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/srv/other");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Single(r => r.Selected).RepoPath).IsEqualTo("/srv/other");
            await Assert.That(repos.Count).IsEqualTo(2);

            await Assert.That(await vm.EnterRemoteRepositoryAsync(@"C:\src\repo")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(@"C:\src\repo");
            await Assert.That(await vm.EnterRemoteRepositoryAsync(@"\\host\share\repo")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(@"\\host\share\repo");
            await Assert.That(await vm.EnterRemoteRepositoryAsync("C:/")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("C:/");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo is null || saved.HarnessByRepo.Count == 0).IsTrue();
        });
    }

    /// Pins that a local repository discovered after a switch to a remote machine is not selected on
    /// that machine, where the path means nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LocalDefaultDiscoveryFinishingAfterARemotePickLeavesItEmpty() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var known = new TaskCompletionSource<string[]>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = [] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                () => known.Task, TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            var discovery = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            known.SetResult(["/local/repo"]);
            await discovery;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// Pins remote path identity to the remote's rules, not this host's: on a case-insensitive host a
    /// typed /w/repo must not fold into an advertised /w/Repo and mark that row selected.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ATypedRemotePathDifferingOnlyInCaseIsItsOwnRow() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/Repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(await vm.EnterRemoteRepositoryAsync("/w/repo")).IsNull();

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/w/Repo", "/w/repo"]);
            await Assert.That(repos.Single(r => r.Selected).RepoPath).IsEqualTo("/w/repo");
        });
    }

    /// Pins the menu to the selected remote as the registry lists it now: once that remote is gone,
    /// neither its selection-time paths nor a same-named local twin's paths are offered.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRepositoryMenuNeverOffersAVanishedRemoteOrALocalTwinsPaths() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m2", Connected = true,
                    RepoPaths = ["/remote/a", "/remote/b"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), localMachineId: "m1");
            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: false);

            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m1", Connected = true,
                    RepoPaths = ["/local/repo"],
                },
            ]);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/remote/a"]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchCarriesTheSelectedMachine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.DaemonName).IsEqualTo("home-pc");
        });
    }

    /// Pins remote failure matching to the remote's case rules: a refusal for /w/Repo does not land
    /// on /w/repo, which a Linux remote holds as a different repository, while one for the path
    /// still selected does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchFailureDoesNotLandOnAPathDifferingOnlyInCase() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/Repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            vm.Goal = "go";
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await vm.StartCommand.Execute();
            await vm.EnterRemoteRepositoryAsync("/w/repo");
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).IsNull();

            launch.Next = new LaunchOutcome(true, "agent-10", null);
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-10", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// The owned-remote list can legitimately empty out from under an already-selected remote
    /// machine (a registry blip) — the picker must stay reachable so the user can switch back to
    /// local, rather than hiding itself with no way to change the selection.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerStaysVisibleForAnActiveRemoteSelectionEvenWhenTheOwnedListEmpties() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await WaitUntilAsync(() => vm.MachinePickerVisible, "picker visible after selecting home-pc");

            remote.DaemonsSubject.OnNext([]);
            await Task.Delay(20); // let the CombineLatest re-run over the now-empty owned list
            await Assert.That(vm.MachinePickerVisible).IsTrue();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.RemoteMachineSelected).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteSelectionRequiresTheLane() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    // Display-list filtering alone doesn't protect the launch path — a name can reach
    // SelectMachineAsync/StartCommand without ever having passed through ListMachinesAsync.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingAnUnownedNameIsRefused() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", Connected = true }]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectRepositoryAsync("/repo/a");

            // "work-mac" never passed ListMachinesAsync's own-daemons filter (it's u2's) — calling
            // SelectMachineAsync with it directly (a stale menu row, a forged binding) must be a
            // no-op, not a silent remote selection.
            await vm.SelectMachineAsync("work-mac", isLocal: false);

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            await Assert.That(vm.SelectedMachine).IsEqualTo(daemon.DaemonName);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ReassignedDaemonOwnerRevokesAnAlreadySelectedMachine() {
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
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            vm.Goal = "go";

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            // Same name, now a different owner — a registry refresh racing the earlier selection.
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u2", Connected = true, RepoPaths = ["/w/repo"] },
            ]);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            // ReactiveCommand.Execute() does not itself gate on CanExecute (verified directly: an
            // unguarded call here reaches StartAsync regardless) — the actual boundary is
            // StartAsync's own fresh ownership re-check, so calling it straight through must still
            // refuse rather than reach the wire.
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.MachineUnavailableMessage);
            await Assert.That(launch.Last).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SwitchingBackToLocalRestoresTheLocalRepoAndRevalidatesTheVendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var store = new HeldAppStateStore(new AppState());
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["kiro"]));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo"], SupportedVendors = ["codex"],
                },
            ]);
            var local = Tmp.CreateDir("local").Path;
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known(local), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            // The adoption must hold the first load before the remote pick, or the pick's own
            // load takes the held one and the switch never returns.
            await store.FirstLoadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(local);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            store.Release();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            // The existing local flow (EnsureDefaultRepositoryAsync) adopts the known repo —
            // never the remote machine's path.
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(local);
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsFalse();
            // codex isn't in the local daemon's advertised set ("kiro") — revalidated, not left at
            // whatever the remote machine last set it to.
            await Assert.That(vm.SelectedVendor).IsEqualTo("kiro");
        });
    }

    /// The remote machine hosts DefaultVendor as well as the vendor the pick kept, so revalidation
    /// alone would accept the late local adoption's reset to DefaultVendor.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ADefaultRepositoryLoadThatLandsAfterARemotePickLeavesItsVendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var store = new HeldAppStateStore(new AppState());
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo"], SupportedVendors = ["claude", "codex"],
                },
            ]);
            var local = Tmp.CreateDir("local").Path;
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known(local), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            // The existence check runs off the UI thread; the adoption must be parked on the held
            // load before the remote pick, or this test proves nothing.
            await store.FirstLoadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(local);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            store.Release();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }
}
