using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// One phase for every "checks running" marker. A style animation starts when its control
/// appears, so the sidebar, reader and rail would each fade on their own offset; these read the
/// same clock instead and stay in step wherever they are shown.
public static class PulseClock {
    const double PeriodSeconds = 2.4;
    const double MinOpacity = 0.4;
    /// Ten frames a second is the whole cost of a pulse: each tick is one render pass of the window.
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsActive", typeof(PulseClock));

    // Active and attached, whether or not visible: hidden ones must be found again when they show.
    static readonly HashSet<Visual> Active = [];
    static readonly long Origin = TimeProvider.System.GetTimestamp();
    static DispatcherTimer? _timer;

    static PulseClock() {
        IsActiveProperty.Changed.AddClassHandler<Visual>((visual, e) => {
            if (e.NewValue is true) {
                visual.AttachedToVisualTree += OnAttached;
                visual.DetachedFromVisualTree += OnDetached;
                // A closed window is still the top level of its old content, so ask the tree.
                if (visual.IsAttachedToVisualTree()) Start(visual);
            } else {
                visual.AttachedToVisualTree -= OnAttached;
                visual.DetachedFromVisualTree -= OnDetached;
                Stop(visual);
            }
        });
        // Effective visibility is already updated when class handlers run, and it changes only
        // through IsVisible or attachment.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Visual>((changed, _) => SyncUnder(changed));
        // A minimized window leaves its content effectively visible.
        Window.WindowStateProperty.Changed.AddClassHandler<Window>((window, _) => SyncUnder(window));
    }

    public static bool GetIsActive(Visual visual) => visual.GetValue(IsActiveProperty);
    public static void SetIsActive(Visual visual, bool value) => visual.SetValue(IsActiveProperty, value);

    internal static bool IsRunning => _timer?.IsEnabled ?? false;

    public static double Opacity {
        get {
            var phase = TimeProvider.System.GetElapsedTime(Origin).TotalSeconds % PeriodSeconds / PeriodSeconds;
            return MinOpacity + (1 - MinOpacity) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
        }
    }

    static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Start((Visual)sender!);
    static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Stop((Visual)sender!);

    static void Start(Visual visual) {
        if (Active.Add(visual)) Tick();
    }

    // A detached marker keeps no reference here.
    static void Stop(Visual visual) {
        if (!Active.Remove(visual)) return;
        visual.ClearValue(Visual.OpacityProperty);
        Tick();
    }

    static void SyncUnder(Visual changed) {
        if (Active.Count == 0) return;
        foreach (var visual in Active) {
            if (visual == changed || changed.IsVisualAncestorOf(visual)) {
                Tick();
                return;
            }
        }
    }

    // A hidden marker stays registered but is not painted, and the timer runs only while one shows,
    // so a collapsed section or a minimized window costs no render passes and no wakeups.
    internal static void Tick() {
        var opacity = Opacity;
        var showing = false;
        foreach (var visual in Active) {
            if (!IsShowing(visual)) continue;
            visual.Opacity = opacity;
            showing = true;
        }
        if (showing) {
            _timer ??= new DispatcherTimer(TickInterval, DispatcherPriority.Render, (_, _) => Tick());
            _timer.Start();
        } else {
            _timer?.Stop();
        }
    }

    static bool IsShowing(Visual visual) =>
        visual.IsEffectivelyVisible && TopLevel.GetTopLevel(visual) is not Window { WindowState: WindowState.Minimized };
}
