using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    /// The launcher's model list prefers the server catalog per vendor and falls back to the
    /// curated list where the server offers none — so an empty or unreachable catalog never blocks
    /// a launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Model_choices_prefer_the_server_catalog_and_fall_back_to_the_curated_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var catalog = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
                ["gemini"] = [new("gemini-3-pro", "Gemini 3 Pro")],
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                modelCatalog: Observable.Return<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>(catalog));

            await Assert.That(vm.ModelChoicesFor("gemini").Select(m => m.Slug)).Contains("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).Contains("claude-opus-5");
            await Assert.That(vm.ModelChoicesFor("cursor")).IsEmpty();
        });
    }

    static IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> ServerFeed(params (string V, string[] Slugs)[] e) =>
        Observable.Return(Server(e));

    [Test]
    public async Task MergeCatalogs_machine_key_wins_even_when_empty_else_non_empty_server_else_absent() {
        var machine = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
            ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["kiro"] = [] };
        var server  = Server(("pi", ["server-pi"]), ("kiro", ["server-kiro"]), ("gemini", ["gemini-3-pro"]), ("cursor", []));

        var merged = HomeViewModel.MergeCatalogs(machine, server);

        await Assert.That(merged["pi"].Single().Slug).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(merged["kiro"]).IsEmpty();
        await Assert.That(merged["gemini"].Single().Slug).IsEqualTo("gemini-3-pro");
        await Assert.That(merged.ContainsKey("cursor")).IsFalse();
        await Assert.That(HomeViewModel.MergeCatalogs(null, server).ContainsKey("pi")).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_snapshot_catalog_takes_precedence_and_empty_key_beats_curated() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("claude", ["server-claude"]), ("pi", ["server-pi"])));

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["claude"] = [] }));

            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("anthropic/claude-opus-5");
            await Assert.That(vm.ModelChoicesFor("claude")).IsEmpty();
            await Assert.That(vm.ModelChoicesFor("codex")).IsNotEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Before_any_local_snapshot_the_server_catalog_alone_populates_the_view() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("gemini", ["gemini-3-pro"])));

            await Assert.That(vm.EffectiveModelCatalog["gemini"].Single().Slug).IsEqualTo("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("pi")).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_older_daemon_without_a_catalog_leaves_the_server_and_curated_lists() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("gemini", ["gemini-3-pro"]), ("claude", [])));

            await Assert.That(vm.ModelChoicesFor("gemini").Single().Slug).IsEqualTo("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).Contains("claude-opus-5");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_remote_machine_supplies_its_own_catalog_and_follows_registry_updates() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("local/model", "Local")] }));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1", "a/x")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                modelCatalog: ServerFeed(("pi", ["server-pi"])));

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/x");

            remote.DaemonsSubject.OnNext([Box("u1", "a/y")]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/y");

            remote.DaemonsSubject.OnNext([]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("server-pi");

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("local/model");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_restored_repository_never_keeps_a_harness_the_daemon_lacks() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    /// Without Claude, a repository with no remembered harness is offered under the selected one,
    /// and picking it keeps that one: the menu never badges a harness the pick would not use.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repository_without_a_remembered_harness_is_badged_with_the_harness_its_pick_keeps() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex", "pi"]));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var a = Tmp.CreateDir("a").Path;
            var b = Tmp.CreateDir("b").Path;
            await vm.SelectRepositoryAsync(a);
            await vm.ChooseHarnessAsync("pi");
            daemon.Agents.AddOrUpdate(Agent("x", b));

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Single(r => r.RepoPath == a).Vendor).IsEqualTo("pi");
            await Assert.That(repos.Single(r => r.RepoPath == b).Vendor).IsEqualTo("pi");

            await vm.SelectRepositoryAsync(b);
            await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_start_with_no_harness_sends_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), launch, Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
            vm.Goal = "go";

            await vm.StartCommand.Execute();

            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_remote_follows_its_live_vendor_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            var box = Box("u1") with { RepoPaths = ["/w/repo"] };
            remote.DaemonsSubject.OnNext([box]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));
            vm.Goal = "go";

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            remote.DaemonsSubject.OnNext([box with { SupportedVendors = [] }]);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.NoHarnessesNotice("box"));

            remote.DaemonsSubject.OnNext([box with { SupportedVendors = ["codex"] }]);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_same_named_daemon_owned_by_someone_else_never_supplies_the_catalog() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u2", "a/z"), Box("u1", "a/x")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                modelCatalog: ServerFeed(("pi", ["server-pi"])));

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/x");

            remote.DaemonsSubject.OnNext([Box("u2", "a/z")]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("server-pi");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_agent_chip_labels_a_model_only_the_machine_lists() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")] }));
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System);

            var text = Capacitor.App.Views.AgentChipTextConverter.Instance.Convert(
                [vm.Harnesses, "pi", "anthropic/claude-opus-5", vm.EffectiveModelCatalog], typeof(string), null,
                System.Globalization.CultureInfo.InvariantCulture);

            await Assert.That(text as string).EndsWith("· Claude Opus 5 · anthropic");
        });
    }

    [Test]
    public async Task ShouldClearSelection_only_when_previously_listed_and_now_absent_under_a_present_key() {
        var listedA  = Server(("pi", ["a", "b"]));
        var withoutA = Server(("pi", ["b"]));
        var noKey    = Server();

        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, withoutA)).IsTrue();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "A", listedA, withoutA)).IsTrue();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "custom", noKey, withoutA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "custom", withoutA, withoutA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", noKey, listedA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, noKey)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, listedA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "", listedA, withoutA)).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_to_a_machine_whose_pi_list_lacks_the_pick_resets_the_model_and_a_custom_id_survives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("a", "A")] }));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1", "b")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.ChooseHarnessAsync("pi");
            vm.SelectedModel = "a";

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
            await Assert.That(vm.SelectedModel).IsEqualTo("");

            vm.SelectedModel = "typed/custom";
            remote.DaemonsSubject.OnNext([Box("u1", "c")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("typed/custom");

            remote.DaemonsSubject.OnNext([Box("u1", "c", "typed/custom")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("typed/custom");
            remote.DaemonsSubject.OnNext([Box("u1", "c")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_refresh_that_drops_the_pick_resets_it_and_a_pick_made_before_any_catalog_survives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System);
            await vm.ChooseHarnessAsync("pi");
            vm.SelectedModel = "a";

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("b", "B")] }));
            await Assert.That(vm.SelectedModel).IsEqualTo("a");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("a", "A"), new("b", "B")] }));
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("b", "B")] }));
            await Assert.That(vm.SelectedModel).IsEqualTo("");
        });
    }
}
