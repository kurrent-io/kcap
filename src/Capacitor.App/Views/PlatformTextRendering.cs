using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// The outermost visual that sets a mode wins, so setting it on each top level covers everything it draws.
public static class PlatformTextRendering {
    static bool _installed;

    public static void Install(TextRenderingMode mode) {
        if (_installed || mode == TextRenderingMode.Unspecified) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TopLevel>((topLevel, _) =>
            TextOptions.SetTextRenderingMode(topLevel, mode));
    }
}
