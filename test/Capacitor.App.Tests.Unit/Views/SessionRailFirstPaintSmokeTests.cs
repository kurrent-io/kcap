using System.Reactive.Linq;
using Avalonia.Headless;
using DynamicData;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.Tests.Helpers.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit.Views;

public class SessionRailFirstPaintSmokeTests {
    static async Task<bool> RenderAsync(TaskCompletionSource painted) {
        for (var i = 0; i < 5 && !painted.Task.IsCompleted; i++) {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.WhenAny(painted.Task, Task.Delay(50));
        }
        return painted.Task.IsCompleted;
    }

    /// Rows in a collapsed rail are data, not paint; the mark waits for the frame that draws them, and fires once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rows_count_as_painted_only_once_a_visible_rail_has_drawn_them() => AvaloniaSession.DispatchAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System) { State = RailState.All };
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Panes.RailCollapsed = true;
        var calls = 0;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(() => {
            Interlocked.Increment(ref calls);
            painted.TrySetResult();
        });
        try {
            window.Show();
            await Assert.That(await RenderAsync(painted)).IsFalse();

            directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto("a", "agent", "claude", "/repo", "Running", null, null, null,
                DateTime.UtcNow, null, null), new RepoIdentity("repo", "repo")));
            await Assert.That(await RenderAsync(painted)).IsFalse();

            window.Panes.RailCollapsed = false;
            await Assert.That(await RenderAsync(painted)).IsTrue();

            directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto("b", "agent", "claude", "/repo", "Running", null, null, null,
                DateTime.UtcNow, null, null), new RepoIdentity("repo", "repo")));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Assert.That(calls).IsEqualTo(1);
        } finally { window.Close(); }
        return true;
    });

    /// Folded groups that fill the viewport leave the one expanded group's row out of sight: nothing
    /// is painted until a row is inside the viewport.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_expanded_group_below_the_viewport_is_not_painted() => AvaloniaSession.DispatchAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System) { State = RailState.All };
        var now = DateTime.UtcNow;
        directory.Rows.AddOrUpdate(Enumerable.Range(0, 40).Select(i => AgentRow.FromLocal(
            new AgentStatusDto($"a{i}", "agent", "claude", $"/repo{i}", "Running", null, null, null, now.AddMinutes(-i), null, null),
            new RepoIdentity($"repo{i}", $"repo{i}"))));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm, Height = 600 };
        window.Panes.RailCollapsed = true;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(() => painted.TrySetResult());
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(rail.Groups.Count).IsEqualTo(40);
            foreach (var group in rail.Groups.Take(rail.Groups.Count - 1)) await group.ToggleCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            window.Panes.RailCollapsed = false;
            await Assert.That(await RenderAsync(painted)).IsFalse();

            await rail.Groups[0].ToggleCommand.Execute();
            await Assert.That(await RenderAsync(painted)).IsTrue();
        } finally { window.Close(); }
        return true;
    });
}
