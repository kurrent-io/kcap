using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// Marks a <see cref="ScrollViewer"/> `:scrolling` while its offset is changing and for a moment after,
/// so its auto-hiding scrollbars can stay out of sight at rest and appear only while it scrolls.
public static class ScrollReveal {
    const string ScrollingClass = ":scrolling";
    static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(900);
    static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> Timers = [];

    public static void Register() =>
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>(OnScrollChanged);

    static void OnScrollChanged(ScrollViewer viewer, ScrollChangedEventArgs e) {
        if (!ReferenceEquals(e.Source, viewer) || e.OffsetDelta == default) {
            return;
        }

        IPseudoClasses classes = viewer.Classes;
        classes.Set(ScrollingClass, true);

        var timer = Timers.GetValue(viewer, v => {
            var t = new DispatcherTimer { Interval = Linger };
            t.Tick += (_, _) => {
                t.Stop();
                ((IPseudoClasses)v.Classes).Set(ScrollingClass, false);
            };
            return t;
        });
        timer.Stop();
        timer.Start();
    }
}
