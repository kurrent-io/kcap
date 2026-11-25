using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// <see cref="MainWindow"/> keeps its menu (off macOS), sidebar toggle, back and forward in one place at the window's
/// top-left; while the rail is collapsed they take a title-bar row over the content, and a header attached
/// here starts below that row. The header keeps its full width and its leading edge, so the title lines up
/// with the content under it rather than with the controls. Outside a MainWindow nothing floats there and the
/// header keeps its own padding.
public static class RailClearance {
    // macOS draws its window buttons at a size the UI zoom does not change, centred 14pt down a
    // 28pt title bar with the last one ending at x=62, and Avalonia offers no way to move them. There
    // the controls are sized to fit that bar and the toggle keeps its 100% window centre, set apart
    // from the buttons, at any zoom; only their size scales.
    static readonly bool BesideWindowButtons = OperatingSystem.IsMacOS();
    const double WindowButtonsMiddle = 14;
    const double CentreBesideWindowButtons = 82;

    public static readonly double ButtonSize = BesideWindowButtons ? 24 : 30;
    public const double Spacing = 4;
    public const double ToggleGap = 10;
    const double Gap = 10;

    /// The toggle's top-left in the content's units, which the UI zoom scales.
    public static Point ButtonOrigin(double scale) => BesideWindowButtons
        ? new(CentreBesideWindowButtons / scale - ButtonSize / 2, WindowButtonsMiddle / scale - ButtonSize / 2)
        : new(12, 7);

    /// How far down a header whose top sits <paramref name="contentTop"/> below the window's top pads to clear
    /// the controls, in content units. Windows already starts the content below its caption buttons, which the
    /// controls share a row with, so there the header's own padding is usually enough.
    public static double Top(double scale, double contentTop) =>
        Math.Max(0, ButtonOrigin(scale).Y + ButtonSize + Gap - contentTop);

    public static void Attach(Control view, Border header) {
        var own = header.Padding;
        MainWindow? window = null;

        void Apply() => header.Padding = window?.RailClearanceTop is > 0 and var top
            ? new Thickness(own.Left, Math.Max(own.Top, top), own.Right, own.Bottom)
            : own;

        void OnChanged(object? sender, EventArgs e) => Apply();

        view.AttachedToVisualTree += (_, _) => {
            window = TopLevel.GetTopLevel(view) as MainWindow;
            if (window is not null) window.RailClearanceChanged += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (window is not null) window.RailClearanceChanged -= OnChanged;
            window = null;
            Apply();
        };
    }
}
