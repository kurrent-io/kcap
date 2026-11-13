using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Stacks the transcript and its live note in one column of at most <see cref="ColumnWidth"/>,
/// centred in the scroll viewport.
public sealed class ChatTailPanel : Panel {
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<ChatTailPanel, double>(nameof(ColumnWidth), double.PositiveInfinity);

    static ChatTailPanel() => AffectsMeasure<ChatTailPanel>(ColumnWidthProperty);

    public double ColumnWidth {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) {
        var column = Math.Min(availableSize.Width, ColumnWidth);
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Children) {
            child.Measure(new Size(column, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var column = Math.Min(finalSize.Width, ColumnWidth);
        var x = (finalSize.Width - column) / 2;
        var y = 0.0;
        foreach (var child in Children) {
            child.Arrange(new Rect(x, y, column, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
