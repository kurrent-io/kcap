using System.Reactive.Subjects;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services.Inbox;
using Capacitor.App.Tests.Unit.ViewModels.Inbox;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Inbox;
using Capacitor.App.Views;
using Capacitor.App.Views.Inbox;
using Capacitor.Remote.Models;
using RxUnit = System.Reactive.Unit;

namespace Capacitor.App.Tests.Unit.Views.Inbox;

/// The bell in a real main window: it sits beside Help once the server offers notifications, names its unread count,
/// and its panel acts on a card through the API.
[NotInParallel("AvaloniaSession")]
public class InboxBellViewTests {
    [Test]
    public Task The_bell_shows_its_count_and_its_panel_marks_a_card_read() => AvaloniaSession.RunWithNativePopupRootsAsync(async () => {
        var api = new FakeInboxApi {
            Count = new(InboxReadKind.Ready, new() { Count = 3 }),
            Page = new(InboxReadKind.Ready, new() {
                UnreadCount = 3,
                Items = [new() { LogicalId = "n1", Version = 2, IsUnread = true, Type = "pass_received", Title = "Pavel passed you work",
                    UpdatedAt = DateTimeOffset.UtcNow, LinkUrl = "/passes/p1", LinkLabel = "Open pass" }],
            }),
        };
        var status = new BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected));
        var settingsOpened = 0;
        using var bell = new InboxBellViewModel(api, status, new Subject<RxUnit>(), _ => { }, () => settingsOpened++);
        var service = new FakeDaemonClientService();
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System) { Inbox = bell };
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var button = rail.FindControl<Button>("RailInboxButton")!;
            await Assert.That(button.IsEffectivelyVisible).IsTrue();
            await Assert.That(AutomationProperties.GetName(button)).IsEqualTo("Notifications, 3 unread");
            await Assert.That(rail.FindControl<Border>("RailInboxBadge")!.IsEffectivelyVisible).IsTrue();

            var flyout = (Flyout)button.Flyout!;
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(bell.IsOpen).IsTrue();
            var panel = (InboxPanelView)flyout.Content!;
            // The card's own binding is what is under test: a synthesized pointer click into the popup does not land
            // reliably on every headless platform.
            var markRead = panel.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Mark read");
            await Assert.That(markRead.IsEffectivelyVisible).IsTrue();
            markRead.Command!.Execute(markRead.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(api.Calls).Contains("read n1@2");

            // The header's own binding is what is under test here: the button runs the bell's command, and the bell
            // closes the panel before settings open.
            var settings = panel.FindControl<Button>("InboxSettingsButton")!;
            await Assert.That(settings.IsEffectivelyVisible).IsTrue();
            settings.Command!.Execute(settings.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(settingsOpened).IsEqualTo(1);
            await Assert.That(flyout.IsOpen).IsFalse();
        } finally {
            window.Close();
        }
    });

    [Test]
    public Task Without_notifications_the_footer_keeps_only_help() => AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Surfaces = new(InboxReadKind.Ready, new UserSurfacesResponseDto { Notifications = false }) };
        var status = new BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected));
        using var bell = new InboxBellViewModel(api, status, new Subject<RxUnit>(), _ => { }, () => { });
        var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System) { Inbox = bell };
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var rail = window.FindDescendantOfType<SessionRailView>()!;
            await Assert.That(rail.FindControl<Button>("RailInboxButton")!.IsEffectivelyVisible).IsFalse();
            await Assert.That(rail.FindControl<Button>("RailHelpButton")!.IsEffectivelyVisible).IsTrue();
        } finally {
            window.Close();
        }
    });
}
