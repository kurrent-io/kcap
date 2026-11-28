using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// Gives every window and popup the platform's text antialiasing (DesktopHost.TextRendering).
/// Set on the top level before its first frame: the outermost visual that specifies a mode wins,
/// so one setting there covers everything the top level draws. Installed once per application.
public static class PlatformTextRendering {
    static bool _installed;

    public static void Install(TextRenderingMode mode) {
        if (_installed || mode == TextRenderingMode.Unspecified) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TopLevel>((topLevel, _) =>
            TextOptions.SetTextRenderingMode(topLevel, mode));
    }
}
