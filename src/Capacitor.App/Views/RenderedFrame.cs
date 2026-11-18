using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Capacitor.App.Views;

public static class RenderedFrame {
    /// Calls <paramref name="rendered"/> once the render thread has finished the next batch for
    /// <paramref name="visual"/>'s compositor. The batch completes whether or not this top level
    /// drew in it; <see cref="WhenDrawn"/> is what proves that. Runs on the render thread. False, and
    /// never called, for a visual not attached to a compositor.
    public static bool After(Visual visual, Action rendered) {
        if (ElementComposition.GetElementVisual(visual)?.Compositor is not { } compositor) return false;

        compositor.RequestCompositionBatchCommitAsync().Rendered
            .ContinueWith(_ => rendered(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    /// Calls <paramref name="drawn"/> once, on the UI thread, with the time the render thread drew a
    /// frame for which <paramref name="shows"/> held throughout: from the layout pass that armed it,
    /// with the top level shown and not minimized the whole way to the render, to the moment the UI
    /// thread heard of it. An attempt a hide or minimize interrupts does not count, and the next
    /// layout pass or show arms another.
    public static void WhenDrawn(Control visual, Func<bool> shows, TimeProvider time, Action<DateTimeOffset> drawn) {
        var done = false;
        var armed = false;
        // A hide or minimize moves this on, so a batch armed before it cannot complete the attempt after it.
        var attempt = 0;
        TopLevel? watched = null;

        bool TopShown() => TopLevel.GetTopLevel(visual) is { IsVisible: true } top && top is not Window { WindowState: WindowState.Minimized };

        void Try() {
            if (done || armed || !TopShown() || !shows()) return;
            var mine = ++attempt;
            armed = After(visual, () => {
                var at = time.GetUtcNow();
                Dispatcher.UIThread.Post(() => {
                    if (done || mine != attempt) return;
                    armed = false;
                    if (!TopShown() || !shows()) {
                        Try();
                        return;
                    }
                    done = true;
                    visual.LayoutUpdated -= OnLayoutUpdated;
                    visual.AttachedToVisualTree -= OnAttached;
                    if (watched is not null) watched.PropertyChanged -= OnTopChanged;
                    drawn(at);
                });
            });
        }

        void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
            if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
            if (TopShown()) Try();
            else {
                attempt++;
                armed = false;
            }
        }

        void Watch() {
            if (TopLevel.GetTopLevel(visual) is not { } top || ReferenceEquals(top, watched)) return;
            if (watched is not null) watched.PropertyChanged -= OnTopChanged;
            watched = top;
            top.PropertyChanged += OnTopChanged;
        }

        void OnLayoutUpdated(object? sender, EventArgs e) => Try();
        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) {
            Watch();
            Try();
        }

        visual.LayoutUpdated += OnLayoutUpdated;
        visual.AttachedToVisualTree += OnAttached;
        Watch();
        Try();
    }
}
