using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Views;
using Lucide.Avalonia;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

/// A paused style animation stays subscribed to Avalonia's animation clock, and one subscriber
/// makes the UI thread render every 16 ms. These pin that a busy indicator the user cannot see
/// holds no subscription.
public class BusyIndicatorTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_spinner_in_a_hidden_section_subscribes_nothing_until_the_section_shows() => RunOnUiAsync(async () => {
        var baseline = await SettledClockSubscribers();
        var spinner = new AppIcon { Kind = LucideIconKind.LoaderCircle };
        BusyIndicator.SetIsBusy(spinner, true);
        var section = new Panel { IsVisible = false, Children = { spinner } };
        var window = new Window { Content = section, Width = 100, Height = 100 };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
            await Assert.That(await SettledClockSubscribers()).IsEqualTo(baseline);

            section.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(spinner.Classes).Contains(BusyIndicator.SpinningClass);
            await Assert.That(ClockSubscribers()).IsGreaterThan(baseline);

            section.IsVisible = false;
            await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
            await Assert.That(await SettledClockSubscribers()).IsEqualTo(baseline);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_visible_spinner_turns_only_while_busy() => RunOnUiAsync(async () => {
        var spinner = new AppIcon { Kind = LucideIconKind.LoaderCircle };
        var window = new Window { Content = spinner, Width = 100, Height = 100 };
        try {
            window.Show();
            await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
            BusyIndicator.SetIsBusy(spinner, true);
            await Assert.That(spinner.Classes).Contains(BusyIndicator.SpinningClass);
            BusyIndicator.SetIsBusy(spinner, false);
            await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_busy_progress_bar_is_indeterminate_only_while_it_shows() => RunOnUiAsync(async () => {
        var bar = new ProgressBar { IsVisible = false };
        BusyIndicator.SetIsBusy(bar, true);
        var window = new Window { Content = bar, Width = 100, Height = 100 };
        try {
            window.Show();
            await Assert.That(bar.IsIndeterminate).IsFalse();
            bar.IsVisible = true;
            await Assert.That(bar.IsIndeterminate).IsTrue();
            window.Content = null;
            await Assert.That(bar.IsIndeterminate).IsFalse();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// A view model can outlive its window and turn busy after the close; the closed window is
    /// still the spinner's top level, but nothing would detach it again to release it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_spinner_turning_busy_after_its_window_closed_stays_still() => RunOnUiAsync(async () => {
        var spinner = new AppIcon { Kind = LucideIconKind.LoaderCircle };
        var window = new Window { Content = spinner, Width = 100, Height = 100 };
        window.Show();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        BusyIndicator.SetIsBusy(spinner, true);
        await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
        BusyIndicator.SetIsBusy(spinner, false);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_minimized_window_stops_its_spinners_until_restored() => RunOnUiAsync(async () => {
        var baseline = await SettledClockSubscribers();
        var spinner = new AppIcon { Kind = LucideIconKind.LoaderCircle };
        BusyIndicator.SetIsBusy(spinner, true);
        var window = new Window { Content = spinner, Width = 100, Height = 100 };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(spinner.Classes).Contains(BusyIndicator.SpinningClass);

            window.WindowState = WindowState.Minimized;
            await Assert.That(spinner.Classes).DoesNotContain(BusyIndicator.SpinningClass);
            await Assert.That(await SettledClockSubscribers()).IsEqualTo(baseline);

            window.WindowState = WindowState.Normal;
            await Assert.That(spinner.Classes).Contains(BusyIndicator.SpinningClass);
        } finally {
            BusyIndicator.SetIsBusy(spinner, false);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    public async Task No_view_starts_a_busy_animation_without_the_indicator() {
        var views = Path.Combine(RepoTree.Root(), "src", "Capacitor.App");
        var offenders = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, i)))
            .Where(x => x.line.Contains("IsIndeterminate=", StringComparison.Ordinal)
                || (x.line.Contains(BusyIndicator.SpinningClass, StringComparison.Ordinal) && !x.line.Contains("Selector=", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(views, x.path)}:{x.i + 1}")
            .ToList();
        await Assert.That(offenders).IsEmpty();
    }

    // Avalonia keeps the clock's subscribers private; a rename after an upgrade fails here, not silently.
    static int ClockSubscribers() {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var context = typeof(Visual).Assembly.GetType("Avalonia.Media.MediaContext")?.GetProperty("Instance", any)?.GetValue(null);
        var clock = context?.GetType().GetField("_clock", any)?.GetValue(context);
        var observers = clock?.GetType().GetField("_observers", any)?.GetValue(clock) as IList;
        return observers?.Count ?? throw new InvalidOperationException("MediaContext._clock._observers is gone; update this probe.");
    }

    // A stopped animation leaves the clock on its next pulse, which is up to one 16 ms render away.
    static async Task<int> SettledClockSubscribers() {
        for (var i = 0; i < 20; i++) {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        return ClockSubscribers();
    }
}
