using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using static Capacitor.Tests.Helpers.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit.Views;

public class MainWindowAppMenuSmokeTests {
    sealed class Host : IDisposable {
        public MainWindow Window { get; }
        public MainWindowViewModel Vm { get; }

        public Host(bool inWindow, RecordingOpener? opener = null) {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap());
            Vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                appMenuInWindow: inWindow, opener: opener);
            Window = new MainWindow { DataContext = Vm };
            new AppMenuBar(opener ?? new RecordingOpener(), () => [Window], () => null, inWindow: true).Attach(Window);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public Button MenuButton => Window.FindControl<Button>("AppMenuButton")!;
        public Flyout Menu => (Flyout)MenuButton.Flyout!;
        public StackPanel Rows => Window.FindControl<StackPanel>("AppMenuRows")!;

        public Button Row(string header) =>
            Rows.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == header);

        public void TapAlt() {
            Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
            Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public Task The_menu_button_shows_only_where_no_native_menu_bar_is_drawn(bool inWindow) => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow);

        await Assert.That(host.MenuButton.IsEffectivelyVisible).IsEqualTo(inWindow);
        await Assert.That(AutomationProperties.GetName(host.MenuButton)).IsEqualTo("Menu");
    });

    /// The rows come from the same native menus macOS draws in its menu bar, in the app, Window and
    /// Help order, with the version beneath them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_menu_draws_the_native_menus_and_runs_their_items() => AvaloniaSession.RunOnUiAsync(async () => {
        var opener = new RecordingOpener();
        using var host = new Host(inWindow: true, opener);
        host.Menu.ShowAt(host.MenuButton);
        Dispatcher.UIThread.RunJobs();

        var headers = host.Rows.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b) ?? "").ToList();
        await Assert.That(headers).Contains("Settings…");
        await Assert.That(string.Join("|", headers.SkipWhile(h => h != "Refresh"))).IsEqualTo(
            "Refresh|Search Sessions…|Back|Forward|Toggle Full Screen|Launch approvals…|kcap Documentation|Changelog|Report a Bug…|Send Feedback…");
        await Assert.That(host.Window.FindControl<TextBlock>("AppMenuVersionText")!.Text).StartsWith("Desktop app ");
        await Assert.That(host.Row("Back").IsEffectivelyEnabled).IsFalse();

        MainWindowSmokeTests.ClickFlyoutButton(host.Row("Changelog"));
        Dispatcher.UIThread.RunJobs();
        await Assert.That(host.Menu.IsOpen).IsFalse();
        await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.ChangelogUrl]);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_tap_of_Alt_opens_the_menu_and_another_closes_it() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow: true);

        host.TapAlt();
        await Assert.That(host.Menu.IsOpen).IsTrue();
        await Assert.That(host.Row("Refresh").IsFocused || host.Rows.Children.OfType<Button>().Any(b => b.IsFocused)).IsTrue();

        host.TapAlt();
        await Assert.That(host.Menu.IsOpen).IsFalse();
    });

    /// Alt+Left steps back; it must not also open the menu when Alt comes up.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_Alt_chord_does_not_open_the_menu() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow: true);

        host.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        host.Window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        host.Window.KeyRelease(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        host.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();

        await Assert.That(host.Menu.IsOpen).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_tap_of_Alt_does_nothing_where_the_native_menu_bar_is_drawn() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow: false);

        host.TapAlt();

        await Assert.That(host.Menu.IsOpen).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Escape_closes_the_menu() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow: true);
        host.TapAlt();
        var focused = host.Rows.Children.OfType<Button>().Single(b => b.IsFocused);

        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = focused });
        Dispatcher.UIThread.RunJobs();

        await Assert.That(host.Menu.IsOpen).IsFalse();
    });

    /// One About window per main window: asking again brings it forward rather than stacking another.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task About_opens_one_window() => AvaloniaSession.RunOnUiAsync(async () => {
        var opener = new RecordingOpener();
        using var host = new Host(inWindow: true, opener);

        host.Window.ShowAbout();
        host.Window.ShowAbout();
        Dispatcher.UIThread.RunJobs();

        var opened = host.Window.OwnedWindows.OfType<AboutWindow>().ToArray();
        await Assert.That(opened.Length).IsEqualTo(1);
        await Assert.That(opened[0].FindControl<TextBlock>("AboutVersionText")!.Text).StartsWith("Version ");
        opened[0].FindControl<Button>("AboutChangelogButton")!.Command!.Execute(null);
        await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.ChangelogUrl]);
    });

    /// A popup no taller than the screen still reaches the last row: the rows scroll above the version line.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_menu_taller_than_its_room_scrolls_to_the_last_row() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new Host(inWindow: true);
        host.Menu.ShowAt(host.MenuButton);
        Dispatcher.UIThread.RunJobs();
        var scroller = (ScrollViewer)host.Rows.Parent!;
        var content = (Grid)host.Menu.Content!;
        content.MaxHeight = 200;
        content.UpdateLayout();

        await Assert.That(scroller.Extent.Height).IsGreaterThan(scroller.Viewport.Height);
        var last = host.Rows.Children.OfType<Button>().Last();
        last.BringIntoView();
        content.UpdateLayout();
        await Assert.That(scroller.Offset.Y).IsGreaterThan(0);
    });
}
