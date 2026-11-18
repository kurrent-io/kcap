using Avalonia;
using Avalonia.Rendering.Composition;

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
}
