using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.Tests.Helpers.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit.Views;

/// The rail list is one Tab stop; arrow keys move through its headers and rows and fold groups.
[NotInParallel("AvaloniaSession")]
public class RailKeyboardSmokeTests {
    static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    static Control? Focused(Window window) => window.FocusManager?.GetFocusedElement() as Control;

    static string? Describe(Control? control) => control?.DataContext switch {
        RailGroupViewModel group => $"header:{group.Label}",
        RailSessionViewModel row => $"row:{row.Primary}",
        _ => control?.Name,
    };

    static async Task WithRailAsync(Func<Window, ItemsControl, Task> body) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        rail.State = RailState.All;
        var now = DateTime.UtcNow;
        AgentRow Row(string id, string title, string repo, int minutesAgo) => AgentRow.FromLocal(
            new AgentStatusDto(id, "agent", "claude", $"/src/{repo}", "Running", null, null, null, now.AddMinutes(-minutesAgo), null, null) { Title = title },
            new RepoIdentity($"repo:acme/{repo}", repo));
        directory.Rows.AddOrUpdate([Row("a", "First", "alpha", 1), Row("b", "Second", "alpha", 2), Row("c", "Third", "beta", 30)]);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        try {
            window.Show();
            Pump(window);
            var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "RailGroups");
            await body(window, list);
        } finally { window.Close(); }
    }

    static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Pump(window);
    }

    [Test]
    public Task Arrow_keys_step_through_headers_and_rows() => AvaloniaSession.RunOnUiAsync(() => WithRailAsync(async (window, list) => {
        var first = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("railRepoHeader"));
        first.Focus(NavigationMethod.Tab);
        Pump(window);
        var seen = new List<string?> { Describe(Focused(window)) };
        for (var i = 0; i < 4; i++) {
            Press(window, PhysicalKey.ArrowDown);
            seen.Add(Describe(Focused(window)));
        }
        await Assert.That(string.Join(" > ", seen)).IsEqualTo("header:alpha > row:First > row:Second > header:beta > row:Third");

        Press(window, PhysicalKey.ArrowUp);
        await Assert.That(Describe(Focused(window))).IsEqualTo("header:beta");
        Press(window, PhysicalKey.Home);
        await Assert.That(Describe(Focused(window))).IsEqualTo("header:alpha");
        Press(window, PhysicalKey.End);
        await Assert.That(Describe(Focused(window))).IsEqualTo("row:Third");
    }));

    [Test]
    public Task Left_folds_a_group_and_right_unfolds_it() => AvaloniaSession.RunOnUiAsync(() => WithRailAsync(async (window, list) => {
        var row = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("railRow"));
        row.Focus(NavigationMethod.Tab);
        Pump(window);
        Press(window, PhysicalKey.ArrowLeft);
        await Assert.That(Describe(Focused(window))).IsEqualTo("header:alpha");
        var group = (RailGroupViewModel)Focused(window)!.DataContext!;

        Press(window, PhysicalKey.ArrowLeft);
        await Assert.That(group.IsExpanded).IsFalse();
        await Assert.That(Describe(Focused(window))).IsEqualTo("header:alpha");
        Press(window, PhysicalKey.ArrowDown);
        await Assert.That(Describe(Focused(window))).IsEqualTo("header:beta").Because("a folded group's rows are skipped");

        Press(window, PhysicalKey.ArrowUp);
        Press(window, PhysicalKey.ArrowRight);
        await Assert.That(group.IsExpanded).IsTrue();
    }));

    [Test]
    public Task Tab_leaves_the_list_from_any_row() => AvaloniaSession.RunOnUiAsync(() => WithRailAsync(async (window, list) => {
        var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railRow")).ToList();
        rows[0].Focus(NavigationMethod.Tab);
        Pump(window);
        Press(window, PhysicalKey.Tab);
        var after = Focused(window);
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.GetVisualAncestors().Contains(list)).IsFalse();
    }));
}
