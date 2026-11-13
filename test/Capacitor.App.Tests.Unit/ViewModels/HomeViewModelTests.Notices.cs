using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_disabled_until_daemon_and_server_are_both_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            vm.Goal = "go";
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            Connect(daemon);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// An update the user went on from early holds local launches until its steps settle, then hands Start back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_waits_for_an_update_that_is_still_finishing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            vm.Goal = "go";
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);
            var progress = new BehaviorSubject<(bool, string?)>((true, "Reconnecting the daemon to the server…"));
            vm.AttachUpdateProgress(progress);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.FinishingUpdateNotice);
            await Assert.That(vm.Notices.ShowUpdate).IsTrue();
            await Assert.That(vm.UpdateDetail).IsEqualTo("Reconnecting the daemon to the server");

            progress.OnNext((false, null));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");
            await Assert.That(vm.Notices.ShowUpdate).IsFalse();
        });
    }

    /// The update changes only this machine, so a remote selection keeps its own notices and launches.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_selection_is_not_held_by_a_local_update() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1") with { RepoPaths = ["/w/repo"] }]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));
            vm.Goal = "go";
            vm.AttachUpdateProgress(new BehaviorSubject<(bool, string?)>((true, "Starting the local server…")));

            await vm.SelectMachineAsync("box", isLocal: false);

            await Assert.That(vm.Notices.ShowUpdate).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_disabled_with_a_notice_when_the_daemon_hosts_no_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            vm.Goal = "go";
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));

            using var lifecycle = new BehaviorSubject<string?>(null);
            vm.AttachDaemonRecovery(ReactiveCommand.Create(() => { }), ReactiveCommand.Create(() => { }),
                Observable.Return(false), Observable.Return(false), lifecycle);
            const string serviceNotice = "Daemon service replacement was skipped. Restart kcap to review it again.";
            lifecycle.OnNext(serviceNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(serviceNotice);
            await Assert.That(vm.BannerDetail).IsNull();
            lifecycle.OnNext(null);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    /// This machine's no-agent banner opens Settings → Harnesses, but only while it is the banner's line and
    /// the app can open Settings. A remote machine's banner never offers it: local settings cannot configure it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_local_no_agent_banner_alone_offers_harnesses_settings() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1") with { RepoPaths = ["/w/repo"], SupportedVendors = [] }]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
            await Assert.That(vm.HarnessesSettingsVisible).IsFalse();

            var opened = 0;
            using var open = new BehaviorSubject<Action?>(() => opened++);
            vm.AttachHarnessesSettings(open);
            await Assert.That(vm.HarnessesSettingsVisible).IsTrue();
            await Assert.That(vm.BannerActionsVisible).IsTrue();
            await vm.OpenHarnessesSettingsCommand.Execute();
            await Assert.That(opened).IsEqualTo(1);

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.NoHarnessesNotice("box"));
            await Assert.That(vm.HarnessesSettingsVisible).IsFalse();
            await Assert.That(vm.BannerActionsVisible).IsFalse();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.HarnessesSettingsVisible).IsTrue();
            open.OnNext(null);
            await Assert.That(vm.HarnessesSettingsVisible).IsFalse();
            await Assert.That(await vm.OpenHarnessesSettingsCommand.CanExecute.FirstAsync()).IsFalse();

            open.OnNext(() => opened++);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            await Assert.That(vm.HarnessesSettingsVisible).IsFalse();
        });
    }

    /// An agent the selected machine does not advertise names that machine and the requirement it lacks, and
    /// a refused launch says the same.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unavailable_agent_says_which_machine_lacks_it_and_why() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1") with { RepoPaths = ["/w/repo"], SupportedVendors = ["codex"] }]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude"]));

            var local = vm.UnavailableHarnessTip(vm.Harnesses.Single(o => o.Vendor == "cursor"));
            await Assert.That(vm.UnavailableHarnessTip(vm.Harnesses.Single(o => o.Vendor == "claude"))).IsNull();
            await Assert.That(local).IsEqualTo(HomeViewModel.HarnessUnavailableReason("Cursor", null));
            await Assert.That(local!).Contains("this machine");
            await Assert.That(local).Contains("command");

            await vm.SelectMachineAsync("box", isLocal: false);
            var onBox = vm.UnavailableHarnessTip(vm.Harnesses.Single(o => o.Vendor == "cursor"));
            await Assert.That(onBox).IsEqualTo(HomeViewModel.HarnessUnavailableReason("Cursor", "box"));
            await Assert.That(onBox!).Contains("box");
            await Assert.That(onBox).DoesNotContain("this machine");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lost_server_connection_tells_the_user_to_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            vm.Goal = "go";

            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.ServerLostNotice);

            // Transient by definition — the retry resolves it or lands on "disconnected".
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "reconnecting"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The lane can go SignedOut (a remote HTTP fetch's own 401, ParkSignedOut) independently of
    /// the local daemon's attach state — the sign-in affordance must surface either way.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneSignedOutSurfacesSignInEvenWithTheLocalDaemonDown() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System, laneStatus: lane.Status);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The app lane can report Connected while the daemon's connection word is still
    /// "disconnected" — tokens landed, the daemon has not caught up. That is still sign-in
    /// catch-up: Sign in would restart a flow that already succeeded. Only a SignedOut park
    /// (or the daemon becoming Ready / down) settles the finishing notice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneConnectedDoesNotAskToSignInWhileDaemonIsStillCatchingUp() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneSignedOutAfterSignInShowsSignInAgain() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            vm.NotifySignInCompleted();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    /// The same signed-out lane that offers Sign in above offers nothing once the server is the
    /// local installation: there is no credential to repair, and the Start banner owns recovery.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_server_never_shows_the_launcher_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.LocalServer).IsSameReferenceAs(local);
        });
    }

    /// The Start banner is the whole story for a stopped local server: the connection banner under
    /// it must neither spin "Connecting", nor tell the user to install an agent, nor, after the
    /// catch-up bound, tell the user to sign in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stopped_local_server_shows_no_connecting_or_sign_in_notice() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected", supportedVendors: []));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(local.Visible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.BannerMessage ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The local server can report down while the daemon still says connected, leaving no raw
    /// notice for the stopped-server rule to hide.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stopped_local_server_shows_no_agents_notice_before_the_daemon_notices() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected", supportedVendors: []));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(local.Visible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    (HomeViewModel Vm, LocalServerRecoveryViewModel Local, FakeServerLane Lane, FakeDaemonClientService Daemon) LocalServerWithRemote(
            bool remoteConnected, List<IDisposable> owned) {
        var daemon = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        remote.DaemonsSubject.OnNext([
            new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = remoteConnected, RepoPaths = ["/w/repo"] },
        ]);
        var lane = new FakeServerLane();
        var vm = new HomeViewModel(
            daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
            daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status,
            appServerUrl: "http://localhost:9999");
        var local = new LocalServerRecoveryViewModel(
            lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
            () => Task.CompletedTask, CancellationToken.None);
        vm.AttachLocalServerRecovery(local);
        owned.Add(vm);
        owned.Add(local);
        Connect(daemon, "connected");

        return (vm, local, lane, daemon);
    }

    /// The Start banner speaks for this Mac only: under a remote pick it must not hide that machine's notice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_pick_keeps_its_notice_while_the_local_server_is_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, _) = LocalServerWithRemote(remoteConnected: true, owned);
                await vm.SelectMachineAsync("home-pc", isLocal: false);
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

                await Assert.That(local.Visible).IsTrue();
                await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// A remote machine that dropped off is that machine's story, not the local server's, and never a sign-in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_offline_remote_pick_under_a_running_local_server_names_the_machine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, _) = LocalServerWithRemote(remoteConnected: false, owned);
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
                await vm.SelectMachineAsync("home-pc", isLocal: false);

                await Assert.That(local.Visible).IsFalse();
                await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.RemoteOfflineNotice("home-pc"));
                await Assert.That(vm.ConnectionNotice!).DoesNotContain("Sign in");
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// Selecting this Mac again restores the local server's own handling.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task This_mac_selected_under_a_down_local_server_leaves_the_start_banner_alone() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, daemon) = LocalServerWithRemote(remoteConnected: true, owned);
                await vm.SelectMachineAsync("home-pc", isLocal: false);
                await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
                Connect(daemon, "disconnected");
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

                await Assert.That(local.Visible).IsTrue();
                await Assert.That(vm.ConnectionNotice).IsNull();
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// A local server takes no sign-in: its refusal must not ask for one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_signed_out_local_server_names_a_restart_not_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            Connect(daemon, "connected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.ConnectionNotice ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.BannerMessage ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.ConnectionNotice).IsNotNull();
        });
    }

    /// A local server takes no sign-in, so no state of its connection may ask for one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_local_server_notice_asks_for_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);
            var shown = new List<string>();
            void Record() => shown.AddRange(new[] { vm.ConnectionNotice, vm.BannerMessage }.OfType<string>());

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            Record();
            time.Advance(HomeViewModel.CatchUpLimit);
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Dormant));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            Record();

            await Assert.That(shown).IsNotEmpty();
            await Assert.That(shown.Where(t => t.Contains("sign in", StringComparison.OrdinalIgnoreCase))).IsEmpty();
        });
    }

    /// Suppressing the server notices must not hide the daemon's own: a local server with the
    /// daemon down still says so.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_server_keeps_the_daemon_down_notice() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
        });
    }

    /// First paint after wizard auth never calls NotifySignInCompleted — Home is constructed
    /// afterwards. A live app lane plus a daemon still saying "disconnected" is catch-up, not
    /// "sign in again".
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALiveAppLaneTreatsDaemonDisconnectAsConnecting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "HTTP://LOCALHOST:9999/");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
                serverUrl: "https://other.example", connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonOnOtherServerNotice("https://other.example", "HTTP://LOCALHOST:9999/"));
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsFalse();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALiveAppLaneStopsTreatingDaemonDisconnectAsConnectingAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task AStuckConnectingDaemonOffersSignInAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "connecting");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CatchUpBoundDoesNotFireIfTheDaemonConnectsInTime() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);

            time.Advance(HomeViewModel.CatchUpLimit - TimeSpan.FromSeconds(1));
            Connect(daemon, "connected");

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(TimeSpan.FromSeconds(2));
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task FinishingSignInAlsoExpiresToSignInAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            vm.NotifySignInCompleted();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    /// A daemon attached by name that serves another server says so, and offers no sign-in, which could
    /// not move it, whatever the app's own lane is doing.
    [Test]
    [Arguments(ServerLaneState.Connecting, "reconnecting")]
    [Arguments(ServerLaneState.Connected, "reconnecting")]
    [Arguments(ServerLaneState.Connected, "connected")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_serving_another_server_says_so_instead_of_asking_for_sign_in(ServerLaneState laneState, string connection) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "https://kurrent.kcap.ai");
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://norton.kcap.ai:443", connection: connection));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(laneState));

            await Assert.That(vm.ConnectionNotice)
                .IsEqualTo("This machine's daemon uses another server. It's set up for norton.kcap.ai, so it can't run agents for kurrent.kcap.ai.");
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// Without both servers known there is no mismatch to report, so a lost connection still asks for sign-in.
    [Test]
    [Arguments(ServerLaneState.Connected, null, "https://other.example")]
    [Arguments(ServerLaneState.Connected, "https://app.example", "")]
    [NotInParallel("AvaloniaSession")]
    public async Task AnUnrelatedAppLaneDoesNotMaskTheLocalServerDisconnection(
            ServerLaneState laneState, string? appServerUrl, string daemonServerUrl) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: appServerUrl);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
                serverUrl: daemonServerUrl, connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(laneState));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();

            vm.NotifySignInCompleted();

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task BannerBusyFollowsTheVisibleMessageAndSelectedMachine(bool afterSignIn) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            if (afterSignIn) vm.NotifySignInCompleted();

            using var startMessage = new BehaviorSubject<string?>(null);
            using var start = ReactiveCommand.Create(() => { });
            using var retry = ReactiveCommand.Create(() => { });
            vm.AttachDaemonRecovery(start, retry, Observable.Return(false), Observable.Return(true), startMessage);
            await Assert.That(vm.BannerBusy).IsTrue();

            const string failure = "Daemon start did not finish. Press Retry.";
            startMessage.OnNext(failure);
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(failure);
            await Assert.That(vm.BannerBusy).IsFalse();

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.BannerMessage).IsNull();
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerBusy).IsFalse();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(failure);
            await Assert.That(vm.BannerBusy).IsFalse();

            startMessage.OnNext(null);
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_daemon_is_not_a_sign_in_problem() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.ConnectionBannerVisible).IsTrue();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// An update's daemon restart is explained and shown as in progress, and the catch-up timer that turns a long
    /// connecting line into "sign in again" does not apply to it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_restarting_after_an_update_is_explained_not_reported_as_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonRestartingNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonRestartingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(HomeViewModel.BusyNotice(HomeViewModel.DaemonRestartingNotice)).IsFalse();
        });
    }

    /// A daemon whose process is up but not yet serving is starting, shown in progress, never as down.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_starting_daemon_is_explained_not_reported_as_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.StartingReason, null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonStartingNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonStartingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();
        });
    }

    /// A remote selection's notices/tooltip/Start-enabled must follow the SELECTED machine's own
    /// availability (RemoteAvailabilityFor), never the local daemon's — a local daemon left down
    /// or disconnected must never leak its own notice into a remote pick, and a lane loss under a
    /// remote pick must still surface something rather than nothing. Switching back to local
    /// restores the legacy local-only notices.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NoticesFollowTheSelectedMachineNotTheLocalDaemon() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            vm.Goal = "go";

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            // Local daemon Unreachable, lane Connected, remote machine Connected: no banner, the
            // local daemon's own down-notice never surfaces, Start is enabled.
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);

            // Back to local: the local daemon's own Unreachable notice applies again.
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
        });
    }

    [Test]
    [Arguments(null, null, null, null)]
    [Arguments("daemon down", null, "daemon down", null)]
    [Arguments("daemon down", "kcap too old", "daemon down", "kcap too old")]
    [Arguments(null, "kcap too old", "kcap too old", null)]
    [Arguments("daemon down", "Wrong server. Run this: `kcap fix`", "Wrong server. Run this: `kcap fix`", null)]
    [Arguments("daemon down", "", "daemon down", null)]
    [Arguments("daemon down", "daemon down", "daemon down", null)]
    [Arguments(HomeViewModel.SignInExpiredNotice, "Reconnecting…", HomeViewModel.SignInExpiredNotice, null)]
    [Arguments(HomeViewModel.SignInExpiredNotice, "kcap too old", HomeViewModel.SignInExpiredNotice, null)]
    public async Task BannerContent_keeps_daemon_output_below_the_explanation(
            string? notice, string? startMessage, string? expectedMessage, string? expectedDetail) {
        var content = HomeViewModel.BannerContentFor(notice, startMessage);
        await Assert.That(content.Message).IsEqualTo(expectedMessage);
        await Assert.That(content.Detail).IsEqualTo(expectedDetail);
    }

    /// Red is for something that broke. A daemon that is merely down, or a start still in flight,
    /// needs the user without anything having failed.
    [Test]
    [Arguments(HomeViewModel.DaemonDownNotice, null, false)]
    [Arguments(HomeViewModel.DaemonDownNotice, "Starting the daemon…", false)]
    [Arguments(HomeViewModel.DaemonDownNotice, "The daemon didn't start. Press Start daemon to try again.", true)]
    [Arguments(HomeViewModel.DaemonIncompatibleNotice, null, true)]
    [Arguments(HomeViewModel.LocalServerRefusedNotice, null, true)]
    [Arguments(HomeViewModel.ServerLostNotice, null, false)]
    [Arguments(HomeViewModel.SignInExpiredNotice, null, false)]
    public async Task Only_a_failure_turns_the_banner_red(string message, string? detail, bool failed) =>
        await Assert.That(HomeViewModel.BannerFailedFor((message, detail))).IsEqualTo(failed);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unauthorized_start_asks_for_sign_in_instead_of_the_raw_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var signInRequests = 0;
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, requestSignIn: () => signInRequests++);
            vm.Goal = "go";
            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            launch.Next = new LaunchOutcome(
                false, null, "Response status code does not indicate success: 401 (Unauthorized).",
                Unauthorized: true);

            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            await vm.SignInCommand.Execute();
            await Assert.That(signInRequests).IsEqualTo(1);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task After_sign_in_a_disconnected_server_shows_finishing_not_sign_in_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_start_clears_the_goal_and_any_previous_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            launch.Next = new LaunchOutcome(false, null, "boom");
            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "go";
            await vm.StartCommand.Execute();

            launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            vm.Goal = "next thing";
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.Goal).IsEqualTo("");
        });
    }

    /// The daemon lifecycle detail and its Start daemon action speak for the local machine: under a
    /// healthy remote pick both yield, and both come back the moment the selection does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalStartMessageBannerYieldsToARemoteSelection() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            const string startMessageText = "App and daemon are incompatible.";
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            using var startMessage = new BehaviorSubject<string?>(startMessageText);
            vm.AttachDaemonRecovery(
                ReactiveCommand.Create(() => { }), ReactiveCommand.Create(() => { }),
                Observable.Return(true), Observable.Return(false), startMessage);

            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(startMessageText);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(vm.BannerMessage).IsNull();
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();
            await Assert.That(vm.DaemonStartVisible).IsFalse();
            await Assert.That(vm.BannerActionsVisible).IsFalse();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(startMessageText);
            await Assert.That(vm.DaemonStartVisible).IsTrue();
            await Assert.That(vm.BannerActionsVisible).IsTrue();
        });
    }
}
