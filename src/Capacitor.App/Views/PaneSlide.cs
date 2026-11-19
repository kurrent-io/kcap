using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Capacitor.App.Views;

/// Collapses and expands a side pane in its grid column. Once the pane is loaded, a change slides it
/// past its window edge at full width while an Auto column follows it.
public sealed class PaneSlide(ColumnDefinition column, Control pane, Dock edge) {
    CancellationTokenSource? _slide;
    bool? _collapsed;

    public void Apply(bool collapsed, double width) {
        if (_collapsed is { } was && was != collapsed && pane.IsLoaded) _ = SlideAsync(collapsed, width);
        else if (_slide is null) Settle(collapsed, width);
        _collapsed = collapsed;
    }

    Thickness Offset(double by) => edge == Dock.Left ? new(-by, 0, 0, 0) : new(0, 0, -by, 0);

    async Task SlideAsync(bool collapsed, double width) {
        var from = pane.IsVisible ? pane.Margin : Offset(width);
        _slide?.Cancel();
        var slide = _slide = new CancellationTokenSource();
        pane.Width = width;
        pane.IsVisible = true;
        column.Width = GridLength.Auto;
        // The animation drops its value when it ends, a frame before the pane is hidden: the end
        // margin is set underneath it so that frame does not flash the open pane.
        var to = Offset(collapsed ? width : 0);
        pane.Margin = to;

        await Motion.Standard.RunAsync(pane, slide.Token, (Layoutable.MarginProperty, from, to));
        if (slide.IsCancellationRequested) return;

        _slide = null;
        pane.ClearValue(Layoutable.WidthProperty);
        Settle(collapsed, width);
    }

    void Settle(bool collapsed, double width) {
        column.Width = new GridLength(collapsed ? 0 : width);
        pane.ClearValue(Layoutable.MarginProperty);
        pane.IsVisible = !collapsed;
    }
}
