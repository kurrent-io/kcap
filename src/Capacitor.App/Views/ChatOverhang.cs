using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Lays its child out over the chat column's right edge by <see cref="AmountProperty"/>, inherited from
/// the chat list, so a right-aligned user turn sits at the window's edge rather than the column's.
public sealed class ChatOverhang : Decorator {
    public static readonly AttachedProperty<double> AmountProperty =
        AvaloniaProperty.RegisterAttached<ChatOverhang, Control, double>("Amount", inherits: true);

    static ChatOverhang() => AffectsMeasure<ChatOverhang>(AmountProperty);

    public static double GetAmount(Control control) => control.GetValue(AmountProperty);
    public static void SetAmount(Control control, double value) => control.SetValue(AmountProperty, value);

    protected override Size MeasureOverride(Size availableSize) {
        if (Child is null) return default;
        var amount = GetAmount(this);
        Child.Measure(availableSize.WithWidth(availableSize.Width + amount));
        return new Size(Math.Max(0, Child.DesiredSize.Width - amount), Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        Child?.Arrange(new Rect(0, 0, finalSize.Width + GetAmount(this), finalSize.Height));
        return finalSize;
    }
}
