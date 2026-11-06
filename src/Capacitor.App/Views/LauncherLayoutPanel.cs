using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The launcher's layers: notices pinned to the top, then the composer centred in the whole pane, then an
/// optional list under it. A notice that would reach the composer pushes it down instead of covering it, so the
/// composer only moves when it has to.
///
/// With the list shown, the composer is placed by its resting height and a fixed reserve for the list, never by
/// what either holds now: a goal that grows, or a list that fills, leaves it where it is. The goal grows the
/// composer downwards, and the list takes the height left under it and scrolls inside it.
public sealed class LauncherLayoutPanel : Panel {
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<LauncherLayoutPanel, double>(nameof(Gap), 16);

    /// The height the composer is centred as if the list took, whatever it holds.
    public static readonly StyledProperty<double> ListReserveProperty =
        AvaloniaProperty.Register<LauncherLayoutPanel, double>(nameof(ListReserve), 240);

    /// The least height the list keeps on a short pane, below which the pane scrolls instead.
    public static readonly StyledProperty<double> ListMinHeightProperty =
        AvaloniaProperty.Register<LauncherLayoutPanel, double>(nameof(ListMinHeight), 140);

    static LauncherLayoutPanel() => AffectsMeasure<LauncherLayoutPanel>(GapProperty, ListReserveProperty, ListMinHeightProperty);

    double? _restHeight;

    public double Gap {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double ListReserve {
        get => GetValue(ListReserveProperty);
        set => SetValue(ListReserveProperty, value);
    }

    public double ListMinHeight {
        get => GetValue(ListMinHeightProperty);
        set => SetValue(ListMinHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) {
        var width = 0.0;
        foreach (var child in Children) {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
        }
        var (top, content, list) = Split();
        var contentHeight = content?.DesiredSize.Height ?? 0;
        // The smallest the composer has been is its resting height: an empty goal, before any text grew it.
        if (content is not null && contentHeight > 0) _restHeight = Math.Min(_restHeight ?? contentHeight, contentHeight);
        var listHeight = Shown(list) ? Gap + Math.Min(list!.DesiredSize.Height, ListMinHeight) : 0;
        return new Size(width, NoticeHeight(top) + contentHeight + listHeight);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var (top, content, list) = Split();
        top?.Arrange(new Rect(0, 0, finalSize.Width, top.DesiredSize.Height));
        if (content is null) return finalSize;

        var height = content.DesiredSize.Height;
        var shown = Shown(list);
        var anchor = shown ? (_restHeight ?? height) + Gap + ListReserve : height;
        var y = Math.Max((finalSize.Height - anchor) / 2, NoticeHeight(top));
        content.Arrange(new Rect(0, y, finalSize.Width, height));

        if (list is null) return finalSize;
        if (!shown) {
            list.Arrange(new Rect(0, y + height, finalSize.Width, 0));
            return finalSize;
        }
        var listTop = y + height + Gap;
        var room = Math.Max(finalSize.Height - listTop, Math.Min(list.DesiredSize.Height, ListMinHeight));
        list.Arrange(new Rect(0, listTop, finalSize.Width, Math.Min(room, list.DesiredSize.Height)));
        return finalSize;
    }

    (Control? Top, Control? Content, Control? List) Split() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null, Children.Count > 2 ? Children[2] : null);

    static bool Shown(Control? list) => list is { IsVisible: true };

    double NoticeHeight(Control? top) =>
        top is { IsVisible: true, DesiredSize.Height: > 0 } ? top.DesiredSize.Height + Gap : 0;
}
