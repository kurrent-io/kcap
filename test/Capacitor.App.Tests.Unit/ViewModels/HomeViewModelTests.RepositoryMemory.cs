using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    /// Repo keys compare the way the filesystem does — so the SAME repository reached under
    /// different casing restores its harness on Windows/macOS, and stays distinct on Linux where
    /// two such paths really are two repositories. Asserting the platform's own answer rather than
    /// one hardcoded expectation is what lets this run on every CI leg.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_keys_compare_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/alpha");

            var expected = OperatingSystem.IsLinux() ? HomeViewModel.DefaultVendor : "codex";
            await Assert.That(vm.SelectedVendor).IsEqualTo(expected);

            // And re-choosing under the other casing must overwrite, not accumulate a second
            // shadowing entry, wherever the two paths are the same repository.
            await vm.ChooseHarnessAsync("pi");
            var saved = await store.LoadAsync();
            var expectedKeys = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(saved.HarnessByRepo!.Count).IsEqualTo(expectedKeys);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Choosing_a_harness_remembers_it_for_that_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_repository_restores_that_repositorys_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/b");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repository_with_no_choice_falls_back_to_the_default_not_the_previous_repo() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/never-seen");

            await Assert.That(vm.SelectedVendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    /// Storage isolation only: ScratchRepoPath ("") is a reserved HarnessByRepo key that must not
    /// share or clobber a real repository's remembered vendor. It says nothing about launching it
    /// — the daemon does not accept a repo-less launch (HomeViewModel.ScratchRepoPath).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_scratch_target_keeps_its_own_remembered_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
            await vm.ChooseHarnessAsync("claude");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo![HomeViewModel.ScratchRepoPath]).IsEqualTo("claude");
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_mode_is_remembered_per_repository_across_launchers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChoosePermissionModeAsync(ClaudePermissionModes.BypassPermissions);
            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(ClaudePermissionModes.BypassPermissions);

            var fresh = Build(out _, out _, path);
            await fresh.SelectRepositoryAsync("/repo/a");
            await Assert.That(fresh.SelectedPermissionMode).IsEqualTo(ClaudePermissionModes.BypassPermissions);
        });
    }

    /// A stored token the catalog does not know falls back to the default rather than riding a launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unknown_stored_permission_mode_restores_the_default() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = Build(out _, out var store, Tmp.PathTo("app-state.json"));
            await store.UpdateAsync(s => s with {
                PermissionModeByRepo = new Dictionary<string, string> { ["/repo/a"] = "yolo" } });

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);
        });
    }

    static HomeViewModel BuildWithBox(string statePath, out FakeDaemonClientService daemon) {
        daemon = new FakeDaemonClientService();
        Connect(daemon);
        var remote = new FakeRemoteAgents();
        remote.DaemonsSubject.OnNext([Box("u1")]);
        return new HomeViewModel(daemon, new AppStateStore(statePath), new RecordingLaunchClient(),
            Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_mode_is_remembered_per_remote_repository_across_launchers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            using var vm = BuildWithBox(path, out _);

            await vm.SelectMachineAsync("box", isLocal: false);
            await vm.EnterRemoteRepositoryAsync("/srv/a");
            await vm.ChoosePermissionModeAsync(ClaudePermissionModes.BypassPermissions);
            await vm.EnterRemoteRepositoryAsync("/srv/A");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);

            await vm.EnterRemoteRepositoryAsync("/srv/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(ClaudePermissionModes.BypassPermissions);

            using var fresh = BuildWithBox(path, out _);
            await fresh.SelectMachineAsync("box", isLocal: false);
            await fresh.EnterRemoteRepositoryAsync("/srv/a");
            await Assert.That(fresh.SelectedPermissionMode).IsEqualTo(ClaudePermissionModes.BypassPermissions);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Harness_is_remembered_per_remote_repository_across_launchers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            using var vm = BuildWithBox(path, out _);

            await vm.SelectMachineAsync("box", isLocal: false);
            await vm.EnterRemoteRepositoryAsync("/srv/a");
            await vm.ChooseHarnessAsync("pi");
            await vm.EnterRemoteRepositoryAsync("/srv/b");
            await vm.ChooseHarnessAsync("claude");

            await vm.EnterRemoteRepositoryAsync("/srv/a");
            await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
            var listed = await vm.ListRepositoriesAsync();
            await Assert.That(listed.Single(o => o.RepoPath == "/srv/a").Vendor).IsEqualTo("pi");

            using var fresh = BuildWithBox(path, out _);
            await fresh.SelectMachineAsync("box", isLocal: false);
            await fresh.EnterRemoteRepositoryAsync("/srv/a");
            await Assert.That(fresh.SelectedVendor).IsEqualTo("pi");
        });
    }

    /// The same path on this machine and on a remote names two different repositories.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_permission_mode_never_restores_for_the_same_local_path() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = BuildWithBox(Tmp.PathTo("app-state.json"), out var daemon);
            await vm.SelectMachineAsync("box", isLocal: false);
            await vm.EnterRemoteRepositoryAsync("/srv/a");
            await vm.ChoosePermissionModeAsync(ClaudePermissionModes.BypassPermissions);

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await vm.SelectRepositoryAsync("/srv/a");

            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);
        });
    }
}
