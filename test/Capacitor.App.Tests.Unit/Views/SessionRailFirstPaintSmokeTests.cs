using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
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
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        rail.State = RailState.All;
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm, Panes = { RailCollapsed = true } };
        var calls = 0;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(TimeProvider.System, _ => {
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
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        rail.State = RailState.All;
        var now = DateTime.UtcNow;
        directory.Rows.AddOrUpdate(Enumerable.Range(0, 40).Select(i => AgentRow.FromLocal(
            new AgentStatusDto($"a{i}", "agent", "claude", $"/repo{i}", "Running", null, null, null, now.AddMinutes(-i), null, null),
            new RepoIdentity($"repo{i}", $"repo{i}"))));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm, Height = 600, Panes = { RailCollapsed = true } };
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(TimeProvider.System, _ => painted.TrySetResult());
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

    static (MainWindow Window, SessionRailViewModel Rail, FakeAgentDirectory Directory) RailWindow(int rows) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var directory = new FakeAgentDirectory();
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System) { State = RailState.All };
        var now = DateTime.UtcNow;
        directory.Rows.AddOrUpdate(Enumerable.Range(0, rows).Select(i => AgentRow.FromLocal(
            new AgentStatusDto($"a{i}", "agent", "claude", $"/repo{i}", "Running", null, null, null, now.AddMinutes(-i), null, null),
            new RepoIdentity($"repo{i}", $"repo{i}"))));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        return (new MainWindow { DataContext = vm, Height = 600 }, rail, directory);
    }

    /// A frame drawn after the window was hidden again is not the rows' paint; showing it again is.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rows_laid_out_in_a_window_hidden_before_it_renders_are_not_painted() => AvaloniaSession.DispatchAsync(async () => {
        var (window, rail, directory) = RailWindow(rows: 1);
        using var _ = directory;
        using var __ = rail;
        window.Panes.RailCollapsed = true;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(TimeProvider.System, _ => painted.TrySetResult());
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Panes.RailCollapsed = false;
            window.UpdateLayout();
            window.Hide();
            await Assert.That(await RenderAsync(painted)).IsFalse();

            window.Show();
            await Assert.That(await RenderAsync(painted)).IsTrue();
        } finally { window.Close(); }
        return true;
    });

    /// A viewport that ends in the gap above an expanded group's first row shows no row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_viewport_ending_above_the_first_row_is_not_painted() => AvaloniaSession.DispatchAsync(async () => {
        var (window, rail, directory) = RailWindow(rows: 40);
        using var _ = directory;
        using var __ = rail;
        window.Panes.RailCollapsed = true;
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionRail.OnFirstSessionsPainted(TimeProvider.System, _ => painted.TrySetResult());
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            foreach (var group in rail.Groups.Take(rail.Groups.Count - 1)) await group.ToggleCommand.Execute();
            window.Panes.RailCollapsed = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var view = window.SessionRail;
            var scroll = view.RailScroll;
            var rows = view.RailGroups.ContainerFromIndex(rail.Groups.Count - 1)!.FindDescendantOfType<ItemsControl>()!;
            var row = ((ContentPresenter)rows.ContainerFromIndex(0)!).Child!;
            var rowTop = row.TranslatePoint(default, scroll)!.Value.Y + scroll.Offset.Y;
            var rowsTop = rows.TranslatePoint(default, scroll)!.Value.Y + scroll.Offset.Y;
            await Assert.That(rowTop).IsGreaterThan(rowsTop);
            await Assert.That(rowTop - 1).IsGreaterThan(scroll.Viewport.Height);

            scroll.Offset = new Vector(0, rowTop - 1 - scroll.Viewport.Height);
            await Assert.That(await RenderAsync(painted)).IsFalse();

            scroll.Offset = new Vector(0, rowTop + 2 - scroll.Viewport.Height);
            await Assert.That(await RenderAsync(painted)).IsTrue();
        } finally { window.Close(); }
        return true;
    });
}
