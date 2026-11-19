using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Capacitor.App.Views;

public static class RenderedFrame {
    /// Calls <paramref name="drawn"/> once, on the UI thread, with the time the render thread drew a
    /// frame of <paramref name="visual"/>'s top level for which <paramref name="shows"/> held
    /// throughout: from the layout pass that armed it, with the top level shown and not minimized the
    /// whole way to the draw, to the moment the UI thread heard of it. An attempt a hide, a minimize or
    /// a layout pass where <paramref name="shows"/> is false interrupts does not count, and the next
    /// qualifying layout pass or show arms another.
    public static void WhenDrawn(Control visual, Func<bool> shows, TimeProvider time, Action<DateTimeOffset> drawn) =>
        new Attempts(visual, shows, time, drawn).Start();

    sealed class Attempts(Control visual, Func<bool> shows, TimeProvider time, Action<DateTimeOffset> drawn) {
        bool _done;
        bool _armed;
        // Moved on by every interruption, so a draw armed before one cannot complete the attempt after it.
        int _attempt;
        TopLevel? _watched;
        CompositionCustomVisual? _probe;

        public void Start() {
            visual.LayoutUpdated += OnLayoutUpdated;
            visual.AttachedToVisualTree += OnAttached;
            Watch();
            Try();
        }

        bool TopShown() => TopLevel.GetTopLevel(visual) is { IsVisible: true } and not Window { WindowState: WindowState.Minimized };

        void Interrupt() {
            _attempt++;
            _armed = false;
        }

        void Try() {
            if (_done || _armed || !TopShown() || !shows() || Probe() is not { } probe) return;
            _armed = true;
            probe.SendHandlerMessage(++_attempt);
        }

        // A batch completes whether or not the top level drew in it: a target whose surface is not
        // ready yet skips the draw. Only a visual's own render proves the frame reached the screen.
        CompositionCustomVisual? Probe() {
            if (ElementComposition.GetElementVisual(visual)?.Compositor is not { } compositor) return null;
            if (_probe?.Compositor == compositor) return _probe;
            _probe = compositor.CreateCustomVisual(new DrawProbe(time, Drew));
            _probe.Size = new Vector(1, 1);
            ElementComposition.SetElementChildVisual(visual, _probe);
            return _probe;
        }

        void Drew(int attempt, DateTimeOffset at) => Dispatcher.UIThread.Post(() => {
            if (_done || attempt != _attempt) return;
            _armed = false;
            if (!TopShown() || !shows()) {
                Try();
                return;
            }
            _done = true;
            visual.LayoutUpdated -= OnLayoutUpdated;
            visual.AttachedToVisualTree -= OnAttached;
            if (_watched is not null) _watched.PropertyChanged -= OnTopChanged;
            ElementComposition.SetElementChildVisual(visual, null);
            drawn(at);
        });

        void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
            if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
            if (TopShown()) Try();
            else Interrupt();
        }

        void Watch() {
            if (TopLevel.GetTopLevel(visual) is not { } top || ReferenceEquals(top, _watched)) return;
            if (_watched is not null) _watched.PropertyChanged -= OnTopChanged;
            _watched = top;
            top.PropertyChanged += OnTopChanged;
        }

        void OnLayoutUpdated(object? sender, EventArgs e) {
            if (_armed && !shows()) Interrupt();
            else Try();
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) {
            Watch();
            Try();
        }
    }

    // Runs on the render thread. Its render is called only when the target draws the region it
    // invalidated, so a target that skips a frame keeps the attempt pending until it does draw.
    sealed class DrawProbe(TimeProvider time, Action<int, DateTimeOffset> drew) : CompositionCustomVisualHandler {
        int _pending;

        public override void OnMessage(object message) {
            if (message is not int attempt) return;
            _pending = attempt;
            Invalidate();
        }

        public override void OnRender(ImmediateDrawingContext context) {
            if (_pending == 0) return;
            var attempt = _pending;
            _pending = 0;
            drew(attempt, time.GetUtcNow());
        }
    }
}
