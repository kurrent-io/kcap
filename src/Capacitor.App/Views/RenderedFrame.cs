using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Capacitor.App.Views;

public static class RenderedFrame {
    // A control has one child-visual slot, so every wait on it shares one probe.
    static readonly ConditionalWeakTable<Control, SharedProbe> Probes = new();

    /// Calls <paramref name="drawn"/> once, on the UI thread, with the time the render thread drew a
    /// frame of <paramref name="visual"/>'s top level for which <paramref name="shows"/> held
    /// throughout: from the layout pass that armed it, with the top level shown and not minimized the
    /// whole way to the draw, to the moment the UI thread heard of it. An attempt a hide, a minimize or
    /// a layout pass where <paramref name="shows"/> is false interrupts does not count, and the next
    /// qualifying layout pass or show arms another. Any number of waits may share one control; a
    /// child visual something else installed on it is refused rather than replaced.
    public static void WhenDrawn(Control visual, Func<bool> shows, TimeProvider time, Action<DateTimeOffset> drawn) {
        var probe = Probes.GetValue(visual, static v => new SharedProbe(v));
        if (probe.Foreign())
            throw new InvalidOperationException($"{visual.GetType().Name} already has a child visual that RenderedFrame did not install.");
        probe.Acquire();
        new Attempts(visual, probe, shows, time, drawn).Start();
    }

    sealed class SharedProbe(Control visual) {
        int _waits;
        CompositionCustomVisual? _probe;

        public bool Foreign() => ElementComposition.GetElementChildVisual(visual) is { } child && !ReferenceEquals(child, _probe);

        public void Acquire() => _waits++;

        public void Release() {
            if (--_waits > 0) return;
            if (_probe is not null && !Foreign()) ElementComposition.SetElementChildVisual(visual, null);
            _probe = null;
            Probes.Remove(visual);
        }

        // A batch completes whether or not the top level drew in it: a target whose surface is not
        // ready yet skips the draw. Only a visual's own render proves the frame reached the screen.
        public CompositionCustomVisual? Get() {
            if (ElementComposition.GetElementVisual(visual)?.Compositor is not { } compositor || Foreign()) return null;
            if (_probe?.Compositor == compositor) return _probe;
            _probe = compositor.CreateCustomVisual(new DrawProbe());
            _probe.Size = new Vector(1, 1);
            ElementComposition.SetElementChildVisual(visual, _probe);
            return _probe;
        }
    }

    sealed class Attempts(Control visual, SharedProbe shared, Func<bool> shows, TimeProvider time, Action<DateTimeOffset> drawn) {
        bool _done;
        bool _armed;
        // Moved on by every interruption, so a draw armed before one cannot complete the attempt after it.
        int _attempt;
        TopLevel? _watched;

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
            if (_done || _armed || !TopShown() || !shows() || shared.Get() is not { } probe) return;
            _armed = true;
            probe.SendHandlerMessage(new Arm(this, ++_attempt, time, Drew));
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
            shared.Release();
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

    sealed record Arm(object Owner, int Attempt, TimeProvider Time, Action<int, DateTimeOffset> Drew);

    // Runs on the render thread. Its render is called only when the target draws the region it
    // invalidated, so a target that skips a frame keeps every armed attempt pending until it does draw.
    sealed class DrawProbe : CompositionCustomVisualHandler {
        readonly Dictionary<object, Arm> _pending = [];

        public override void OnMessage(object message) {
            if (message is not Arm arm) return;
            _pending[arm.Owner] = arm;
            Invalidate();
        }

        public override void OnRender(ImmediateDrawingContext context) {
            if (_pending.Count == 0) return;
            var armed = _pending.Values.ToArray();
            _pending.Clear();
            foreach (var arm in armed) arm.Drew(arm.Attempt, arm.Time.GetUtcNow());
        }
    }
}
