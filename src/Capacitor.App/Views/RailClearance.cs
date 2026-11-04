using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// While the session rail is collapsed, <see cref="MainWindow"/> floats its show-sidebar button over
/// the content's top-left corner; a header attached here pads its leading edge clear of it. Outside a
/// MainWindow nothing floats there and the header keeps its own padding.
public static class RailClearance {
    // macOS draws its window buttons at a size the UI zoom does not change, centred 14pt down a
    // 28pt title bar with the last one ending at x=62. There the button keeps its 100% window
    // centre, one slot past them, at any zoom; only its size scales.
    static readonly bool BesideWindowButtons = OperatingSystem.IsMacOS();
    const double WindowButtonsMiddle = 14;
    const double CentreBesideWindowButtons = 86;

    const double ButtonSize = 30;
    const double Gap = 10;

    /// The button's top-left in the content's units, which the UI zoom scales.
    public static Point ButtonOrigin(double scale) => BesideWindowButtons
        ? new(CentreBesideWindowButtons / scale - ButtonSize / 2, WindowButtonsMiddle / scale - ButtonSize / 2)
        : new(12, 7);

    /// How far a header at the content's leading edge pads to clear the button, in content units.
    public static double Leading(double scale) => ButtonOrigin(scale).X + ButtonSize + Gap;

    public static void Attach(Control view, Border header) {
        var own = header.Padding;
        MainWindow? window = null;

        void Apply() => header.Padding = window?.RailLeading is > 0 and var leading
            ? new Thickness(Math.Max(own.Left, leading), own.Top, own.Right, own.Bottom)
            : own;

        void OnChanged(object? sender, EventArgs e) => Apply();

        view.AttachedToVisualTree += (_, _) => {
            window = TopLevel.GetTopLevel(view) as MainWindow;
            if (window is not null) window.RailLeadingChanged += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (window is not null) window.RailLeadingChanged -= OnChanged;
            window = null;
            Apply();
        };
    }
}
