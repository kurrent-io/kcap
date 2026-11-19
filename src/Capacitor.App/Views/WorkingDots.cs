using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// A 3x3 dot grid lit by a diagonal wave: the mark of an agent that is progressing. Every instance reads one clock,
/// so rows that started working at different moments still wave in step.
public sealed class WorkingDots : Control {
    const int Cells = 3;
    const double PeriodSeconds = 1.2;
    const double GapRatio = 0.14;
    /// Twenty frames a second keeps the wave smooth at 12px; each tick is one render pass.
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<WorkingDots, IBrush?>(nameof(Foreground));

    static readonly HashSet<WorkingDots> Active = [];
    static readonly long Origin = TimeProvider.System.GetTimestamp();
    static DispatcherTimer? _timer;

    static WorkingDots() => AffectsRender<WorkingDots>(ForegroundProperty);

    public IBrush? Foreground {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Sync();
    }

    // Idle rows keep the control attached but hidden; only shown ones hold the timer.
    void Sync() {
        if (IsVisible && TopLevel.GetTopLevel(this) is not null) {
            Active.Add(this);
            _timer ??= new DispatcherTimer(TickInterval, DispatcherPriority.Render, (_, _) => Tick());
            _timer.Start();
        } else if (Active.Remove(this) && Active.Count == 0) {
            _timer?.Stop();
        }
    }

    static void Tick() {
        foreach (var dots in Active) {
            if (dots.IsEffectivelyVisible) dots.InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context) {
        if (Foreground is not { } brush) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var gap = size * GapRatio;
        var dot = (size - gap * (Cells - 1)) / Cells;
        var phase = TimeProvider.System.GetElapsedTime(Origin).TotalSeconds % PeriodSeconds / PeriodSeconds;
        for (var y = 0; y < Cells; y++) {
            for (var x = 0; x < Cells; x++) {
                // Cells light in order of their distance from the top-left corner.
                var local = phase - (double)(x + y) / (2 * (Cells - 1));
                var wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * local);
                var radius = dot / 2 * (0.7 + 0.3 * wave);
                var centre = new Point(x * (dot + gap) + dot / 2, y * (dot + gap) + dot / 2);
                using (context.PushOpacity(0.2 + 0.8 * wave))
                    context.DrawEllipse(brush, null, centre, radius, radius);
            }
        }
    }
}
