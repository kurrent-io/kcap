using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

public class RenderedFrameSmokeTests {
    static async Task RenderAsync(int ticks = 5) {
        for (var i = 0; i < ticks; i++) {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// A window hidden again before its frame renders, as one held behind an update's progress is,
    /// is not counted as shown; its next show is.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_window_hidden_before_it_renders_counts_from_its_next_show() => AvaloniaSession.DispatchAsync(async () => {
        var window = new Window { Width = 400, Height = 300 };
        var shown = 0;
        try {
            window.Show();
            RenderedFrame.WhenDrawn(window, () => true, TimeProvider.System, _ => shown++);
            window.Hide();
            await RenderAsync();
            await Assert.That(shown).IsEqualTo(0);

            window.Show();
            await RenderAsync();
            await Assert.That(shown).IsEqualTo(1);

            window.Hide();
            window.Show();
            await RenderAsync();
            await Assert.That(shown).IsEqualTo(1);
        } finally { window.Close(); }
        return true;
    });

    /// An attempt whose predicate fails on a layout pass does not count, even when the predicate holds
    /// again by the time the frame draws with no layout pass between; the next pass where it holds arms another.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_layout_where_the_predicate_fails_interrupts_the_attempt() => AvaloniaSession.DispatchAsync(async () => {
        var window = new Window { Width = 400, Height = 300 };
        var holds = true;
        var shown = 0;
        try {
            window.Show();
            RenderedFrame.WhenDrawn(window, () => holds, TimeProvider.System, _ => shown++);
            holds = false;
            window.Width = 401;
            for (var i = 0; i < 3; i++) {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            var layouts = 0;
            window.LayoutUpdated += (_, _) => layouts++;
            holds = true;
            await RenderAsync();
            await Assert.That(layouts).IsEqualTo(0);
            await Assert.That(shown).IsEqualTo(0);

            window.Width = 402;
            await RenderAsync();
            await Assert.That(shown).IsEqualTo(1);
        } finally { window.Close(); }
        return true;
    });
}
