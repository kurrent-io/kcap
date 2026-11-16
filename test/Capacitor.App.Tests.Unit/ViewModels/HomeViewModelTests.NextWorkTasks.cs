using Avalonia.Controls;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    /// A picked next-work task launches from an empty composer with its line and the server's context, and what is
    /// typed goes between them; the composer's placeholder says so, and a launch that sends the task unpicks it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_task_sends_its_line_what_is_typed_and_its_context_then_unpicks() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = Build(out var launch, out _, Tmp.PathTo("state.json"));
            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", null, true, Tmp.Path, "Recorded context"));

            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.GoalPlaceholder).IsEqualTo(HomeViewModel.PickedGoalPlaceholder);
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.Prompt).IsEqualTo("A-1 Fix it\n\nRecorded context");
            await Assert.That(vm.NextWorkSource).IsNull();
            await Assert.That(vm.GoalPlaceholder).IsEqualTo(HomeViewModel.DefaultGoalPlaceholder);

            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", null, true, Tmp.Path, "Recorded context"));
            vm.Goal = "Keep the public API as it is";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.Prompt).IsEqualTo("A-1 Fix it\n\nKeep the public API as it is\n\nRecorded context");
            await Assert.That(launch.Last!.NextWorkTargetKey).IsEqualTo("wi:a");
        });
    }

    /// A pull request to review is no next-work launch target: the server claims and attaches only work items and loose ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_review_launches_without_a_next_work_target() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = Build(out var launch, out _, Tmp.PathTo("state.json"));
            await vm.FillFromNextWorkAsync(new("pr:o/r#9", "#9 Retry", "Review pull request #9 Retry.", null, true, Tmp.Path));
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.NextWorkTargetKey).IsNull();
        });
    }

    /// A task whose repository has no known checkout clears a repository chosen for something else, and Start refuses
    /// until its own is picked, so the task never runs in an unrelated checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_without_a_checkout_clears_the_chosen_repository_and_waits_for_its_own() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = Build(out var launch, out _, Tmp.PathTo("state.json"));
            var unrelated = Tmp.CreateDir("unrelated");
            await vm.SelectRepositoryAsync(unrelated);

            await vm.FillFromNextWorkAsync(new("le:b", "Document B", "Document B", null, true, null, "Recorded context", AwaitsCheckout: true));
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.PickTaskRepository);
        });
    }

    /// A task on a machine the launcher cannot switch to (not yet listed as the viewer's) never takes that machine's path
    /// as a local one: it waits for its checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_machine_switch_leaves_the_task_waiting_for_its_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = Build(out var launch, out _, Tmp.PathTo("state.json"));
            await vm.SelectRepositoryAsync(Tmp.CreateDir("unrelated"));

            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", "studio", false, "/home/maya/payments", "Recorded context"));

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
            await Assert.That(vm.NextWorkSource!.AwaitsCheckout).IsTrue();
        });
    }

    /// Default discovery, at startup or when the repository menu opens, never fills the empty selection a task waiting
    /// for its checkout holds: Start keeps refusing until the person picks one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Default_discovery_leaves_a_task_waiting_for_its_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var launch = new RecordingLaunchClient();
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var recent = Tmp.CreateDir("recent").Path;
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("state.json")), launch,
                Known(recent), TimeProvider.System);
            await WaitUntilAsync(() => vm.SelectedRepoPath == recent, "the startup default adopted");

            await vm.FillFromNextWorkAsync(new("le:b", "Document B", "Document B", null, true, null, "Recorded context", AwaitsCheckout: true));
            await vm.EnsureDefaultRepositoryAsync();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.PickTaskRepository);
        });
    }

    /// A pick of no repository made while discovery awaits its reads wins, though the selection reads as it did.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Default_discovery_yields_to_a_pick_of_no_repository_made_while_it_reads() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var known = new TaskCompletionSource<string[]>();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("state.json")), new RecordingLaunchClient(),
                () => known.Task, TimeProvider.System);

            var discovery = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
            known.SetResult([Tmp.CreateDir("recent").Path]);
            await discovery;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// Discovery started before a task pick or during it never fills the empty selection the pick makes: the pick
    /// parks on the held store between selecting no repository and recording the task, and discovery resumes there.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Default_discovery_never_fills_the_selection_of_a_task_being_picked(bool discoveryFirst) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var launch = new RecordingLaunchClient();
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var store = new HeldAppStateStore(new AppState());
            var known = new TaskCompletionSource<string[]>();
            using var vm = new HomeViewModel(daemon, store, launch, () => known.Task, TimeProvider.System);

            var discovery = discoveryFirst ? vm.EnsureDefaultRepositoryAsync() : null;
            var pick = vm.FillFromNextWorkAsync(new("le:b", "Document B", "Document B", null, true, null, "Recorded context", AwaitsCheckout: true));
            discovery ??= vm.EnsureDefaultRepositoryAsync();
            known.SetResult([Tmp.CreateDir("recent").Path]);
            await discovery;
            store.Release();
            await pick;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.PickTaskRepository);
        });
    }

    /// A picked task's checkout is its own: once its directory is deleted, opening the menu leaves the task picked,
    /// as the same pick a launch in flight will consume, and Start refusing, never running it in another repository.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_tasks_deleted_checkout_leaves_it_waiting_for_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var launch = new RecordingLaunchClient();
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var other = Tmp.CreateDir("other").Path;
            var checkout = Tmp.CreateDir("checkout").Path;
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("state.json")), launch, Known(other), TimeProvider.System);
            await WaitUntilAsync(() => vm.SelectedRepoPath == other, "the startup default adopted");
            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", daemon.DaemonName, true, checkout, "Recorded context"));
            var picked = vm.NextWorkSource;
            Directory.Delete(checkout);

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
            await Assert.That(ReferenceEquals(vm.NextWorkSource, picked)).IsTrue();
            await Assert.That(repos.Any(r => r.RepoPath == checkout)).IsFalse();
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.PickTaskRepository);
        });
    }

    /// A picked task whose selection was deleted goes back to its own checkout, not to a more recent repository.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_task_falls_back_to_its_own_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var recent = Tmp.CreateDir("recent").Path;
            var checkout = Tmp.CreateDir("checkout").Path;
            var chosen = Tmp.CreateDir("chosen").Path;
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("state.json")), new RecordingLaunchClient(),
                Known(recent), TimeProvider.System);
            await WaitUntilAsync(() => vm.SelectedRepoPath == recent, "the startup default adopted");
            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", daemon.DaemonName, true, checkout, "Recorded context"));
            await vm.SelectRepositoryAsync(chosen);
            Directory.Delete(chosen);

            await vm.EnsureDefaultRepositoryAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(checkout);
        });
    }

    /// A task with no repository leaves discovery as it was: one that was reading when the task was picked still
    /// adopts the default.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_without_a_repository_picked_while_discovery_reads_still_gets_the_default() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var known = new TaskCompletionSource<string[]>();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("state.json")), new RecordingLaunchClient(),
                () => known.Task, TimeProvider.System);
            var recent = Tmp.CreateDir("recent").Path;

            var discovery = vm.EnsureDefaultRepositoryAsync();
            await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", null, true, null, "Recorded context"));
            known.SetResult([recent]);
            await discovery;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(recent);
        });
    }

    /// Emptying the composer leaves the task picked; only its chip unpicks it.
    [Test]
    public async Task Emptying_the_composer_keeps_the_task_picked() {
        using var vm = Build(out _, out _, Tmp.PathTo("state.json"));
        await vm.FillFromNextWorkAsync(new("wi:a", "A-1 Fix it", "A-1 Fix it", null, true, null, "Recorded context"));
        vm.Goal = "x";
        vm.Goal = "";
        await Assert.That(vm.NextWorkSource).IsNotNull();

        vm.ClearNextWorkSource();
        await Assert.That(vm.NextWorkSource).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Folder_access_recovery_preserves_the_draft_and_clears_only_after_access() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = Build(out var launch, out _, Tmp.PathTo("state.json"));
            await vm.SelectRepositoryAsync(Tmp.Path);
            vm.Goal = "Keep this draft";
            launch.Next = new(false, null, $"Access to the path '{Tmp.Path}' is denied.");
            await vm.StartCommand.Execute();
            await Assert.That(vm.CanRequestRepositoryAccess).IsTrue();
            await AvaloniaSession.DispatchAsync(() => {
                var view = new Capacitor.App.Views.LauncherPaneView { DataContext = vm };
                var window = new Avalonia.Controls.Window { Content = view, Width = 1000, Height = 720 };
                try {
                    window.Show();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    if (!view.FindControl<Avalonia.Controls.Button>("RequestRepositoryAccessButton")!.IsVisible)
                        throw new InvalidOperationException("Folder recovery action is hidden");
                    if (Environment.GetEnvironmentVariable("KCAP_ACCESS_SHOT") is { } shot) {
                        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
                        frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    }
                } finally { window.Close(); }
            });
            await vm.RequestRepositoryAccessAsync(_ => Task.FromResult(false));
            await Assert.That(vm.StartError).IsNotNull();
            await Assert.That(vm.CanRequestRepositoryAccess).IsTrue();
            var vendor = vm.SelectedVendor;
            var model = vm.SelectedModel;
            var mode = vm.SelectedPermissionMode;
            await vm.RequestRepositoryAccessAsync(_ => throw new UnauthorizedAccessException());
            await Assert.That(vm.StartError).IsNotNull();
            await vm.RequestRepositoryAccessAsync(_ => Task.FromResult(true));
            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.SelectedVendor).IsEqualTo(vendor);
            await Assert.That(vm.SelectedModel).IsEqualTo(model);
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(mode);
            await Assert.That(vm.Goal).IsEqualTo("Keep this draft");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(Tmp.Path);
        });
    }
}
