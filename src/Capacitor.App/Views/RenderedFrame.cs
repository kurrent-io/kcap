using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Capacitor.App.Views;

public static class RenderedFrame {
    /// Calls <paramref name="rendered"/> once the render thread has drawn everything laid out so far
    /// in <paramref name="visual"/>'s top level. It runs on the render thread, so it should only take
    /// a timestamp and hand the rest off. A visual not attached to a compositor never calls it.
    public static void After(Visual visual, Action rendered) {
        if (ElementComposition.GetElementVisual(visual)?.Compositor is not { } compositor) return;

        compositor.RequestCompositionBatchCommitAsync().Rendered
            .ContinueWith(_ => rendered(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// Calls <paramref name="shown"/> once, on the UI thread, with the time the render thread drew the
    /// first frame of <paramref name="window"/> that it was still showing once the UI thread heard of
    /// it. A frame drawn for a window hidden before it rendered does not count; the next show tries again.
    public static void FirstShownFrame(Window window, TimeProvider time, Action<DateTimeOffset> shown) {
        var done = false;
        void Arm() => After(window, () => {
            var at = time.GetUtcNow();
            Dispatcher.UIThread.Post(() => {
                if (done || !window.IsVisible) return;
                done = true;
                window.Opened -= OnOpened;
                shown(at);
            });
        });
        void OnOpened(object? sender, EventArgs e) => Arm();
        window.Opened += OnOpened;
        if (window.IsVisible) Arm();
    }
}
