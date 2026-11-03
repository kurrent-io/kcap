using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// While the session rail is collapsed, <see cref="MainWindow"/> floats its show-sidebar button over
/// the content's top-left corner; a header attached here pads its leading edge clear of it. Outside a
/// MainWindow nothing floats there and the header keeps its own padding.
public static class RailClearance {
    // macOS draws its window buttons over the window's top-left corner.
    public static readonly double ButtonLeft = OperatingSystem.IsMacOS() ? 78 : 12;

    // A kcapIcon button is 30 wide; the rest is the gap before the header's first content.
    public static double Leading => ButtonLeft + 30 + 10;

    public static Thickness Clear(Thickness padding) =>
        new(Math.Max(padding.Left, Leading), padding.Top, padding.Right, padding.Bottom);

    public static void Attach(Control view, Border header) {
        var expanded = header.Padding;
        PaneLayout? panes = null;

        void Apply() => header.Padding = panes?.RailCollapsed == true ? Clear(expanded) : expanded;

        void OnChanged(object? sender, EventArgs e) => Apply();

        view.AttachedToVisualTree += (_, _) => {
            panes = (TopLevel.GetTopLevel(view) as MainWindow)?.Panes;
            if (panes is not null) panes.Changed += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (panes is not null) panes.Changed -= OnChanged;
            panes = null;
            Apply();
        };
    }
}
