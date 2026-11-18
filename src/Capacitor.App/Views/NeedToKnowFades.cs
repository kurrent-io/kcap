using Avalonia;
using Avalonia.Media;
using Capacitor.App.NeedToKnow;

namespace Capacitor.App.Views;

/// Replaces a level's fade brushes: the text colour, and the inline-code background, at the given opacity.
static class NeedToKnowFades {
    static readonly Color Text = Color.Parse("#F1F3F7");
    static readonly Color CodeBackground = Color.Parse("#191D27");

    public static string TextKey(NeedToKnowLevel level) => level == NeedToKnowLevel.Footnote ? "NeedToKnowFootnoteFadeBrush" : "NeedToKnowDetailFadeBrush";

    public static string CodeKey(NeedToKnowLevel level) => level == NeedToKnowLevel.Footnote ? "NeedToKnowFootnoteCodeFadeBrush" : "NeedToKnowDetailCodeFadeBrush";

    public static void Set(NeedToKnowLevel level, double percent) {
        if (Application.Current is not { } app) return;
        var alpha = (byte)Math.Round(Math.Clamp(percent / 100, 0, 1) * 255);
        app.Resources[TextKey(level)] = new SolidColorBrush(Color.FromArgb(alpha, Text.R, Text.G, Text.B));
        app.Resources[CodeKey(level)] = new SolidColorBrush(Color.FromArgb(alpha, CodeBackground.R, CodeBackground.G, CodeBackground.B));
    }
}
