using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class HistoryImportBannerSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public Task Keyboard_dismissal_survives_import_completion_and_hiding_the_window() => AvaloniaSession.RunOnUiAsync(async () => {
        var gate = new TaskCompletionSource<StreamingResult>();
        var run = new HistoryImportRun(1, 1, "recent history", 1, action => Dispatcher.UIThread.Post(action));
        run.Start(new FakeKcapCli { ImportBehavior = (_, _, _) => gate.Task },
            [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        var service = ReadyService();
        using var home = new HomeViewModel(service, new AppStateStore(Tmp.PathTo("app-state.json")), new NoLaunch(),
            () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            home: home, historyImport: run, requestSetup: () => { });
        var window = new MainWindow { DataContext = vm, Width = WindowSizeMemory.MinWidth, Height = WindowSizeMemory.MinHeight };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var banner = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "HistoryImportBanner");
            var dismiss = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "HistoryImportDismissButton");
            await Assert.That(banner.IsEffectivelyVisible).IsTrue();
            dismiss.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(banner.IsEffectivelyVisible).IsFalse();
            await Assert.That(window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "GoalInput").IsFocused).IsTrue();
            await Assert.That(run.Running).IsTrue();
            window.Hide();
            gate.SetResult(new StreamingResult(0, false, []));
            await run.Completion;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(banner.IsEffectivelyVisible).IsFalse();
        } finally {
            gate.TrySetResult(new StreamingResult(0, false, []));
            await run.Completion;
            window.Close();
        }
    });

    [Test]
    public Task A_failed_import_reopens_setup_from_its_keyboard_action() => AvaloniaSession.RunOnUiAsync(async () => {
        var run = new HistoryImportRun(1, 1, "recent history", 1, action => action());
        run.Start(new FakeKcapCli { ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, [])) },
            [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        await run.Completion;
        var service = ReadyService();
        using var home = new HomeViewModel(service, new AppStateStore(Tmp.PathTo("app-state.json")), new NoLaunch(),
            () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
        var reopened = 0;
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            home: home, historyImport: run, requestSetup: () => reopened++);
        var window = new MainWindow { DataContext = vm, Width = WindowSizeMemory.MinWidth, Height = WindowSizeMemory.MinHeight };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var reopen = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "HistoryImportSetupButton");
            await Assert.That(reopen.IsEffectivelyVisible).IsTrue();
            var dismiss = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "HistoryImportDismissButton");
            dismiss.Focus();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            await Assert.That(reopen.IsFocused).IsTrue();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            await Assert.That(dismiss.IsFocused).IsTrue();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(reopened).IsEqualTo(1);
        } finally {
            window.Close();
        }
    });
    [Test]
    [Arguments(280, 0)]
    [Arguments(280, 1)]
    [Arguments(822, 0)]
    [Arguments(822, 1)]
    public Task Finishing_an_import_preserves_the_composer_and_keeps_actions_reachable(int width, int exitCode) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource<StreamingResult>();
            var run = new HistoryImportRun(1000, 1, "recent history", 1, action => Dispatcher.UIThread.Post(action));
            run.Start(new FakeKcapCli { ImportBehavior = (_, _, _) => gate.Task },
                [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
            var reopened = 0;
            var presentation = new HistoryImportBannerViewModel(run, () => reopened++);
            using var watch = presentation.Watch();
            var service = ReadyService();
            using var home = new HomeViewModel(service, new AppStateStore(Tmp.PathTo("app-state.json")),
                new NoLaunch(), () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
            var launcher = new LauncherPaneView { DataContext = home, HistoryImport = presentation };
            var window = new Window { Width = width, Height = WindowSizeMemory.MinHeight, Content = launcher };
            try {
                window.Show();
                Settle(window);
                var banner = launcher.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "HistoryImportBanner");
                var goal = launcher.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "GoalCard");
                var before = goal.TranslatePoint(default, launcher)!.Value.Y;
                var bannerHeight = banner.Bounds.Height;
                service.StatusSubject.OnNext(new(AttachState.Unreachable, "unreachable", null));
                Settle(window);
                await Assert.That(banner.IsEffectivelyVisible).IsFalse();
                await Assert.That(launcher.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "ConnectionBanner").IsEffectivelyVisible).IsTrue();
                gate.SetResult(new StreamingResult(exitCode, false, []));
                await run.Completion;
                Settle(window);

                await Assert.That(presentation.Running).IsFalse();
                await Assert.That(banner.IsEffectivelyVisible).IsFalse();
                await Assert.That(run.BannerDismissed).IsFalse();
                service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
                Settle(window);
                await Assert.That(banner.IsEffectivelyVisible).IsTrue();
                await Assert.That(banner.Bounds.Height).IsEqualTo(bannerHeight);
                await Assert.That(goal.TranslatePoint(default, launcher)!.Value.Y).IsEqualTo(before);
                var name = exitCode == 0 ? "HistoryImportDismissButton" : "HistoryImportSetupButton";
                var action = launcher.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
                var origin = action.TranslatePoint(default, banner)!.Value;
                await Assert.That(origin.X).IsGreaterThanOrEqualTo(0);
                await Assert.That(origin.X + action.Bounds.Width).IsLessThanOrEqualTo(banner.Bounds.Width);
                await Assert.That(origin.Y + action.Bounds.Height).IsLessThanOrEqualTo(banner.Bounds.Height);
                action.Focus();
                await Assert.That(action.IsFocused).IsTrue();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle(window);
                if (exitCode == 0) await Assert.That(banner.IsEffectivelyVisible).IsFalse();
                else await Assert.That(reopened).IsEqualTo(1);
            } finally {
                gate.TrySetResult(new StreamingResult(0, false, []));
                await run.Completion;
                window.Close();
            }
        });

    static FakeDaemonClientService ReadyService() {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        return service;
    }

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
    sealed class NoLaunch : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("History status must not launch an agent.");
    }

}
