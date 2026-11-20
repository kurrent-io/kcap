using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Rendering.Composition;
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

    /// Waits on one control each complete on their own predicate: one armed after another does not take
    /// the first's probe, and those finishing do not detach the probe a later one still waits on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Concurrent_waits_on_one_control_each_complete_on_their_own_predicate() => AvaloniaSession.DispatchAsync(async () => {
        var window = new Window { Width = 400, Height = 300 };
        var gated = false;
        var first = 0;
        var second = 0;
        var third = 0;
        try {
            window.Show();
            await RenderAsync(1);
            RenderedFrame.WhenDrawn(window, () => true, TimeProvider.System, _ => first++);
            RenderedFrame.WhenDrawn(window, () => true, TimeProvider.System, _ => second++);
            RenderedFrame.WhenDrawn(window, () => gated, TimeProvider.System, _ => third++);
            window.Width = 401;
            await RenderAsync();
            await Assert.That(first).IsEqualTo(1);
            await Assert.That(second).IsEqualTo(1);
            await Assert.That(third).IsEqualTo(0);

            gated = true;
            window.Width = 402;
            await RenderAsync();
            await Assert.That(third).IsEqualTo(1);
            await Assert.That(ElementComposition.GetElementChildVisual(window)).IsNull();
        } finally { window.Close(); }
        return true;
    });

    /// A child visual something else installed is neither replaced nor removed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_foreign_child_visual_is_refused_and_left_in_place() => AvaloniaSession.DispatchAsync(async () => {
        var window = new Window { Width = 400, Height = 300 };
        try {
            window.Show();
            await RenderAsync(1);
            var foreign = ElementComposition.GetElementVisual(window)!.Compositor.CreateContainerVisual();
            ElementComposition.SetElementChildVisual(window, foreign);

            await Assert.That(() => RenderedFrame.WhenDrawn(window, () => true, TimeProvider.System, _ => { })).Throws<InvalidOperationException>();
            await Assert.That(ElementComposition.GetElementChildVisual(window)).IsSameReferenceAs(foreign);
        } finally { window.Close(); }
        return true;
    });
}
