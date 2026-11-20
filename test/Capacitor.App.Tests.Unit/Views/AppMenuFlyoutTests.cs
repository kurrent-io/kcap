using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

public class AppMenuFlyoutTests {
    static string Layout(Panel rows) =>
        string.Join("|", rows.Children.Select(c => c is Button b ? AutomationProperties.GetName(b) : "-"));

    static Button Row(Panel rows, string header) =>
        rows.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == header);

    /// Each menu is a section; a separator shows only between two drawn rows, so hidden items and
    /// a section with nothing visible never leave a doubled, leading or trailing line.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sections_and_separators_follow_the_visible_items() {
        var layout = await AvaloniaSession.DispatchAsync(() => {
            var first = new NativeMenu {
                new NativeMenuItem("Hidden") { IsVisible = false }, new NativeMenuItemSeparator(),
                new NativeMenuItem("One"), new NativeMenuItemSeparator(), new NativeMenuItemSeparator(), new NativeMenuItem("Two"),
                new NativeMenuItemSeparator(),
            };
            var empty = new NativeMenu { new NativeMenuItem("Gone") { IsVisible = false } };
            var nested = new NativeMenuItem("Nested") { Menu = new NativeMenu { new NativeMenuItem("Inner") } };
            var last = new NativeMenu { new NativeMenuItemSeparator(), new NativeMenuItem("Three"), nested };
            var rows = new StackPanel();
            using var _ = AppMenuFlyout.Populate(rows, [first, empty, last], () => { });
            return Layout(rows);
        });

        await Assert.That(layout).IsEqualTo("One|-|Two|-|Three");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_row_follows_its_item_enabled_state_until_disposed() {
        var states = await AvaloniaSession.DispatchAsync(() => {
            var item = new NativeMenuItem("Settings…") { IsEnabled = false };
            var rows = new StackPanel();
            var bindings = AppMenuFlyout.Populate(rows, [new NativeMenu { item }], () => { });
            var row = Row(rows, "Settings…");
            var before = row.IsEnabled;
            item.IsEnabled = true;
            var live = row.IsEnabled;
            bindings.Dispose();
            item.IsEnabled = false;
            return (before, live, row.IsEnabled);
        });

        await Assert.That(states).IsEqualTo((false, true, true));
    }

    /// The flyout closes before the item runs, since an item may open a window over it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_click_closes_then_runs_the_item_and_its_command() {
        var order = await AvaloniaSession.DispatchAsync(() => {
            var order = new List<string>();
            var item = new NativeMenuItem("Refresh") { Command = new RecordingCommand(() => order.Add("command")) };
            item.Click += (_, _) => order.Add("click");
            var rows = new StackPanel();
            using var _ = AppMenuFlyout.Populate(rows, [new NativeMenu { item }], () => order.Add("close"));
            Row(rows, "Refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return string.Join("|", order);
        });

        await Assert.That(order).IsEqualTo("close|click|command");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_row_shows_its_shortcut_beside_the_label() {
        var (shown, accelerator) = await AvaloniaSession.DispatchAsync(() => {
            var rows = new StackPanel();
            using var _ = AppMenuFlyout.Populate(rows,
                [new NativeMenu { new NativeMenuItem("Settings…") { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control) } }], () => { });
            var row = Row(rows, "Settings…");
            var texts = ((Grid)row.Content!).Children.OfType<TextBlock>().Select(t => t.Text);
            return (string.Join("|", texts), AutomationProperties.GetAcceleratorKey(row));
        });

        await Assert.That(shown).IsEqualTo("Settings…|Ctrl+,");
        await Assert.That(accelerator).IsEqualTo("Ctrl+,");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Arrow_focus_skips_disabled_rows_and_wraps() => AvaloniaSession.RunOnUiAsync(async () => {
        var rows = new StackPanel();
        var window = new Window { Content = rows };
        using var _ = AppMenuFlyout.Populate(rows,
            [new NativeMenu { new NativeMenuItem("A"), new NativeMenuItem("Off") { IsEnabled = false }, new NativeMenuItem("B") }], () => { });
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            string? Focused() => rows.Children.OfType<Button>().SingleOrDefault(b => b.IsFocused) is { } b ? AutomationProperties.GetName(b) : null;

            AppMenuFlyout.MoveFocus(rows, down: true);
            await Assert.That(Focused()).IsEqualTo("A");
            AppMenuFlyout.MoveFocus(rows, down: true);
            await Assert.That(Focused()).IsEqualTo("B");
            AppMenuFlyout.MoveFocus(rows, down: true);
            await Assert.That(Focused()).IsEqualTo("A");
            AppMenuFlyout.MoveFocus(rows, down: false);
            await Assert.That(Focused()).IsEqualTo("B");
        } finally {
            window.Close();
        }
    });

    sealed class RecordingCommand(Action run) : System.Windows.Input.ICommand {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
