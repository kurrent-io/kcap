using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Remote.Models;
using static Capacitor.Tests.Helpers.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit.Views;

/// A screen reader hears a stale row's state in the row's own name, not only in its hover tip.
[NotInParallel("AvaloniaSession")]
public class RailRowAccessibilitySmokeTests {
    [Test]
    public Task A_stale_row_announces_what_stale_means_and_a_down_lane_in_its_name() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        rail.State = RailState.All;
        var now = DateTimeOffset.UtcNow;
        directory.Rows.AddOrUpdate(AgentRow.FromServer(new RepoSessionDto {
            SessionId = "s1", Status = "active", AccessLevel = "full", Vendor = "claude", Title = "Quiet session",
            StartedAt = now.AddHours(-3), LastActivityAt = now.AddHours(-2), Stale = true,
            Repo = new() { Hash = "h", Owner = "acme", Name = "alpha" },
        }, hostedPresentation: false));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        try {
            window.Show();
            Pump(window);
            var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("railRow"));
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            await Assert.That(peer.GetName()).IsEqualTo("Quiet session, Stale: No activity for over an hour");

            directory.RemoteStale.OnNext(true);
            Pump(window);
            await Assert.That(peer.GetName())
                .IsEqualTo($"Quiet session, Stale: No activity for over an hour, {RailSessionViewModel.LaneStaleNote}");
        } finally { window.Close(); }
    });

    static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }
}
