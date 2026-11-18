using Avalonia;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// Replaces a need-to-know fade with the text colour at the given opacity.
static class NeedToKnowFades {
    public const string WorthKey = "NeedToKnowWorthFadeBrush";
    public const string CuriousKey = "NeedToKnowCuriousFadeBrush";

    static readonly Color Text = Color.Parse("#F1F3F7");

    public static void Set(string key, double percent) {
        if (Application.Current is not { } app) return;
        var alpha = (byte)Math.Round(Math.Clamp(percent / 100, 0, 1) * 255);
        app.Resources[key] = new SolidColorBrush(Color.FromArgb(alpha, Text.R, Text.G, Text.B));
    }
}
