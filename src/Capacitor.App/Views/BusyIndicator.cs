using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Starts a busy indicator's animation only while it is busy and can be seen: a spinning icon
/// gets the <c>kcapSpinning</c> class, a progress bar <c>IsIndeterminate</c>. A style animation
/// stays subscribed to Avalonia's animation clock while its control is hidden, and one subscriber
/// makes the UI thread render every 16 ms, so a spinner in a hidden banner keeps an idle app awake.
/// Set <see cref="IsBusyProperty"/> instead of either of those directly.
public static class BusyIndicator {
    public const string SpinningClass = "kcapSpinning";

    public static readonly AttachedProperty<bool> IsBusyProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsBusy", typeof(BusyIndicator));

    // Busy and attached, whether or not visible: hidden ones must be found again when they show.
    static readonly HashSet<Visual> Busy = [];

    static BusyIndicator() {
        IsBusyProperty.Changed.AddClassHandler<Visual>((visual, e) => {
            if (e.NewValue is true) {
                visual.AttachedToVisualTree += OnAttached;
                visual.DetachedFromVisualTree += OnDetached;
                if (TopLevel.GetTopLevel(visual) is not null) Busy.Add(visual);
            } else {
                visual.AttachedToVisualTree -= OnAttached;
                visual.DetachedFromVisualTree -= OnDetached;
                Busy.Remove(visual);
            }
            Sync(visual);
        });
        // Effective visibility is already updated when class handlers run, and it changes only
        // through IsVisible or attachment.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Visual>((_, _) => {
            if (Busy.Count == 0) return;
            foreach (var visual in Busy.ToArray()) Sync(visual);
        });
    }

    public static bool GetIsBusy(Visual visual) => visual.GetValue(IsBusyProperty);
    public static void SetIsBusy(Visual visual, bool value) => visual.SetValue(IsBusyProperty, value);

    static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        var visual = (Visual)sender!;
        Busy.Add(visual);
        Sync(visual);
    }

    static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) {
        var visual = (Visual)sender!;
        Busy.Remove(visual);
        Sync(visual);
    }

    static void Sync(Visual visual) {
        var animate = Busy.Contains(visual) && visual.IsEffectivelyVisible;
        if (visual is ProgressBar bar) {
            if (bar.IsIndeterminate != animate) bar.IsIndeterminate = animate;
        } else {
            visual.Classes.Set(SpinningClass, animate);
        }
    }
}
