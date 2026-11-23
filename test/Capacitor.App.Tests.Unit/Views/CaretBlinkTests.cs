using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

/// Every caret blink wakes the render loop, so a focused text box nobody is typing in must stop
/// blinking, and must still show its caret while it does.
public class CaretBlinkTests {
    static readonly TimeSpan Blinking = TextBox.CaretBlinkIntervalProperty.GetDefaultValue(typeof(TextBox));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_idle_focused_box_shows_a_steady_caret_until_the_next_key() => RunOnUiAsync(async () => {
        var (window, _, presenter) = Show();
        try {
            await Assert.That(presenter.CaretBlinkInterval).IsEqualTo(Blinking);

            CaretBlink.Settle();
            await Assert.That(presenter.CaretBlinkInterval).IsEqualTo(TimeSpan.Zero);
            await Assert.That(CaretDrawn(presenter)).IsTrue();

            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            await Assert.That(presenter.CaretBlinkInterval).IsEqualTo(Blinking);
            await Assert.That(CaretDrawn(presenter)).IsTrue();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_box_that_loses_focus_while_steady_blinks_again_when_refocused() => RunOnUiAsync(async () => {
        var other = new Button { Content = "x" };
        var (window, box, presenter) = Show(other);
        try {
            CaretBlink.Settle();
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(CaretDrawn(presenter)).IsFalse();

            box.Focus();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(presenter.CaretBlinkInterval).IsEqualTo(Blinking);
            await Assert.That(CaretDrawn(presenter)).IsTrue();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_box_without_focus_is_left_alone() => RunOnUiAsync(async () => {
        var other = new Button { Content = "x" };
        var (window, _, presenter) = Show(other);
        try {
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            CaretBlink.Settle();
            await Assert.That(presenter.CaretBlinkInterval).IsEqualTo(Blinking);
            await Assert.That(CaretDrawn(presenter)).IsFalse();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_presenter_replaced_while_steady_stays_steady_and_the_old_one_stays_stopped() => RunOnUiAsync(async () => {
        var (window, box, stale) = Show();
        try {
            CaretBlink.Settle();
            var template = box.Template;
            box.Template = null;
            Dispatcher.UIThread.RunJobs();
            box.Template = template;
            box.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();
            var replacement = box.GetVisualDescendants().OfType<TextPresenter>().Single();
            await Assert.That(replacement).IsNotSameReferenceAs(stale);
            await Assert.That(BlinkTimerRunning(replacement)).IsFalse();
            await Assert.That(CaretDrawn(replacement)).IsTrue();

            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            await Assert.That(BlinkTimerRunning(stale)).IsFalse();
            await Assert.That(BlinkTimerRunning(replacement)).IsTrue();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    static (Window Window, TextBox Box, TextPresenter Presenter) Show(Control? sibling = null) {
        var box = new TextBox();
        var panel = new StackPanel { Children = { box } };
        if (sibling is not null) panel.Children.Add(sibling);
        var window = new Window { Content = panel, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, box, box.GetVisualDescendants().OfType<TextPresenter>().Single());
    }

    // Avalonia keeps the caret's phase private; a rename after an upgrade fails here, not silently.
    static bool CaretDrawn(TextPresenter presenter) =>
        typeof(TextPresenter).GetField("_caretBlink", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(presenter) as bool?
        ?? throw new InvalidOperationException("TextPresenter._caretBlink is gone; update this probe.");

    static bool BlinkTimerRunning(TextPresenter presenter) =>
        typeof(TextPresenter).GetField("_caretTimer", BindingFlags.Instance | BindingFlags.NonPublic) is { } field
            ? field.GetValue(presenter) is DispatcherTimer { IsEnabled: true }
            : throw new InvalidOperationException("TextPresenter._caretTimer is gone; update this probe.");
}
