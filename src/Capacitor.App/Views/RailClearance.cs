using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// While the session rail is collapsed, <see cref="MainWindow"/> floats its show-sidebar button over
/// the content's top-left corner; a header attached here pads its leading edge clear of it. Outside a
/// MainWindow nothing floats there and the header keeps its own padding.
public static class RailClearance {
    // macOS draws its window buttons over the window's top-left corner at a size the UI zoom does not
    // change, so there the button keeps its 100% window position and only its own size scales.
    static readonly bool BesideWindowButtons = OperatingSystem.IsMacOS();
    const double WindowButtonsEnd = 78;
    const double TitleBandMiddle = 22;

    const double ButtonSize = 30;
    const double Gap = 10;

    /// The button's top-left in the content's units, which the UI zoom scales.
    public static Point ButtonOrigin(double scale) => BesideWindowButtons
        ? new(WindowButtonsEnd / scale, Math.Max(0, TitleBandMiddle / scale - ButtonSize / 2))
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
