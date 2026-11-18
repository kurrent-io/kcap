using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Layout;
using Avalonia.Media;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// Shared UI icon: a full 24px Lucide canvas scaled into a square slot, with round 2px
/// strokes. Use Size (12 for dense status rows, 14 by default, 16 for actions), rather
/// than stretching geometry bounds. Vendor logos and product branding stay separate.
/// Filled paints the icon's own shape in its foreground under the stroke, for closed shapes only.
public sealed class AppIcon : LucideIcon {
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<AppIcon, bool>(nameof(Filled));

    static AppIcon() => AffectsRender<AppIcon>(FilledProperty);

    public AppIcon() {
        Size = 14;
        StrokeWidth = 2;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    (LucideIconKind Kind, Geometry Shape)? _fill;

    public bool Filled {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public override void Render(DrawingContext context) {
        if (Filled && Kind is { } kind) {
            if (_fill?.Kind != kind) _fill = (kind, Geometry.Parse(kind.GetGeometryData()));
            var scale = Size / 24;
            using (context.PushTransform(Matrix.CreateScale(scale, scale)))
                context.DrawGeometry(Foreground, null, _fill.Value.Shape);
        }
        base.Render(context);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AppIconAutomationPeer(this);
}
