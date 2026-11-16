using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_merges_remembered_repos_and_agent_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var aPath = Tmp.CreateDir("a").Path;
            var bPath = Tmp.CreateDir("b").Path;
            await vm.SelectRepositoryAsync(aPath);
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", bPath));

            var repos = await vm.ListRepositoriesAsync();

            var a = repos.Single(r => r.RepoPath == aPath);
            var b = repos.Single(r => r.RepoPath == bPath);
            await Assert.That(a.Vendor).IsEqualTo("codex");
            await Assert.That(b.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
            await Assert.That(a.Selected).IsTrue();
            await Assert.That(b.Selected).IsFalse();
        });
    }

    /// Same platform-follows-filesystem rule as the harness-memory test above: a daemon-supplied
    /// path and a remembered key differing only in case are one repository on Windows/macOS and
    /// two on Linux. This merge is exactly the case that motivated PathComparer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            // On a case-insensitive filesystem the second is the first again.
            var upper = Tmp.CreateDir("Alpha").Path;
            var lower = Tmp.CreateDir("alpha").Path;
            await vm.SelectRepositoryAsync(upper);
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", lower));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            var expected = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(repos.Count).IsEqualTo(expected);
            // The remembered key is the one the user picked, so its casing is the one displayed.
            await Assert.That(repos.Any(r => r.RepoPath == upper)).IsTrue();
        });
    }

    /// Trailing separators are spelling, not identity — a remembered path and a known/agent path
    /// that differ only by `/` must stay one menu row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_trailing_separators() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var repo = Tmp.CreateDir("kcap-cli").Path;
            var spelled = repo + Path.DirectorySeparatorChar;
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                () => Task.FromResult(new[] { spelled }), TimeProvider.System);

            await vm.SelectRepositoryAsync(repo);
            daemon.Agents.AddOrUpdate(Agent("x", spelled));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo(repo);
            await Assert.That(repos[0].Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Scratch_appears_only_when_no_real_repositories() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var empty = await vm.ListRepositoriesAsync();
            await Assert.That(empty.Count).IsEqualTo(1);
            await Assert.That(empty[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);

            var b = Tmp.CreateDir("b").Path;
            daemon.Agents.AddOrUpdate(Agent("x", b));
            var withRepo = await vm.ListRepositoriesAsync();

            await Assert.That(withRepo.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(withRepo.Single(r => r.RepoPath == b).Selected).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(b);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_selection_adopts_the_most_recent_known_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var newer = Tmp.CreateDir("newer").Path;
            var older = Tmp.CreateDir("older").Path;
            // GetSortedPathsAsync is last-used first — index 0 is the most recent.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known(newer, older), TimeProvider.System);

            await vm.EnsureDefaultRepositoryAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(newer);
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(repos.Single(r => r.RepoPath == newer).Selected).IsTrue();
        });
    }

    /// Deleting a clone touches none of the menu's sources — repos.json, the remembered harness, an
    /// ended agent — so each would offer it forever unless the menu checks the disk itself.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_deleted_repository_leaves_the_menu_whichever_source_names_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var kept = Tmp.CreateDir("kept").Path;
            var remembered = Tmp.CreateDir("remembered").Path;
            var agentRepo = Tmp.CreateDir("agent").Path;
            var known = Tmp.PathTo("known");
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(kept, known), TimeProvider.System);
            await vm.SelectRepositoryAsync(remembered);
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync(kept);
            daemon.Agents.AddOrUpdate(Agent("x", agentRepo));

            Directory.Delete(remembered);
            Directory.Delete(agentRepo);
            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(new[] { kept });
            var local = (await vm.ListMachinesAsync()).Single(m => m.IsLocal);
            await Assert.That(local.RepoPaths).IsEquivalentTo(new[] { kept });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_selection_skips_a_deleted_most_recent_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var older = Tmp.CreateDir("older").Path;
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(Tmp.PathTo("deleted"), older), TimeProvider.System);

            await vm.EnsureDefaultRepositoryAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(older);
        });
    }

    /// The launcher can sit on a repository while its clone is deleted; the next menu open moves it
    /// to one that exists, or to no repository when none does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selection_whose_directory_was_deleted_is_replaced() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var other = Tmp.CreateDir("other").Path;
            var picked = Tmp.CreateDir("picked").Path;
            var known = new List<string> { other };
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                () => Task.FromResult(known.ToArray()), TimeProvider.System);
            await vm.SelectRepositoryAsync(picked);

            Directory.Delete(picked);
            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(other);
            await Assert.That(repos.Single(r => r.RepoPath == other).Selected).IsTrue();

            known.Clear();
            Directory.Delete(other);
            repos = await vm.ListRepositoriesAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
            await Assert.That(repos.Single().Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task EnsureDefault_does_not_overwrite_a_pick_made_during_discovery() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var release = new TaskCompletionSource();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                async () => {
                    await release.Task;
                    return ["/repo/default"];
                }, TimeProvider.System);

            var ensure = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectRepositoryAsync("/repo/user-picked");
            release.SetResult();
            await ensure;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/user-picked");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_without_a_repository_contributes_no_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.Agents.AddOrUpdate(Agent("x", null));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// A repository picked through the folder dialog but not yet remembered (and with no agent in
    /// it) must still appear — otherwise closing and reopening the menu would lose it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_current_selection_appears_even_when_unsaved_and_agentless() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var picked = Tmp.CreateDir("fresh").Path;
            await vm.SelectRepositoryAsync(picked);

            var repos = await vm.ListRepositoriesAsync();

            var fresh = repos.Single(r => r.RepoPath == picked);
            await Assert.That(fresh.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repositories_are_ordered_by_leaf_name() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var bravo = Tmp.CreateDir("x", "bravo").Path;
            var alpha = Tmp.CreateDir("y", "alpha").Path;
            daemon.Agents.AddOrUpdate(Agent("x", bravo));
            daemon.Agents.AddOrUpdate(Agent("y", alpha));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos[0].RepoPath).IsEqualTo(alpha);
            await Assert.That(repos[1].RepoPath).IsEqualTo(bravo);
        });
    }

    /// A reviewer launched into a requester's worktree reports that worktree as its RepoPath —
    /// the menu must offer the repository, never the agent's checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agents_worktree_path_is_listed_as_its_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var repo = Tmp.CreateDir("a").Path;
            daemon.Agents.AddOrUpdate(Agent("x", Path.Combine(repo, ".claude", "worktrees", "leafy")));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Any(r => r.RepoPath == repo)).IsTrue();
            await Assert.That(repos.Any(r => r.RepoPath.Contains("worktrees"))).IsFalse();
        });
    }

    /// The daemon's persisted known-repos store (repos.json — the same list DaemonConnect.RepoPaths
    /// feeds the server's launch dialog) is a source of its own: a repo you once ran an agent in
    /// must appear with no live agent and no locally remembered harness.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_includes_the_daemons_persisted_known_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var recorded = Tmp.CreateDir("recorded").Path;
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(),
                Known(recorded), TimeProvider.System);

            var repos = await vm.ListRepositoriesAsync();

            var known = repos.Single(r => r.RepoPath == recorded);
            await Assert.That(known.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_known_repo_dedups_against_a_remembered_key_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            // On a case-insensitive filesystem the second is the first again.
            var upper = Tmp.CreateDir("Alpha").Path;
            var lower = Tmp.CreateDir("alpha").Path;
            var beta = Tmp.CreateDir("beta").Path;
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known(lower, beta), TimeProvider.System);

            await vm.SelectRepositoryAsync(upper);
            await vm.ChooseHarnessAsync("codex");

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            // beta only exists in the store, so it proves the source is wired; alpha collides
            // with the remembered key wherever the filesystem says they are one repository.
            await Assert.That(repos.Any(r => r.RepoPath == beta)).IsTrue();
            var expected = OperatingSystem.IsLinux() ? 3 : 2;
            await Assert.That(repos.Count).IsEqualTo(expected);
            await Assert.That(repos.Any(r => r.RepoPath == upper)).IsTrue();
        });
    }

    /// The daemon's advertised vendor set is what narrows the picker, end to end: a snapshot
    /// arriving on the Snapshots stream must reach the bound Harnesses list.
    /// HostedHarnessCatalogTests covers Build in isolation; this covers the wire into it, which
    /// nothing else did.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_snapshot_narrows_the_harness_picker() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            // Before any snapshot: capability unknown, so everything is offered.
            var piBefore = vm.Harnesses.Single(h => h.Vendor == "pi").Available;

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));

            await Assert.That(piBefore).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "pi").Available).IsFalse();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsTrue();
        });
    }
}
