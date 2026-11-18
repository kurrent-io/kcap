using Avalonia;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// Replaces a need-to-know grey with one a fraction of the way from the canvas to the text colour.
static class NeedToKnowGreys {
    public const string WorthKey = "NeedToKnowWorthGreyBrush";
    public const string CuriousKey = "NeedToKnowCuriousGreyBrush";

    static readonly Color Canvas = Color.Parse("#0B0D12");
    static readonly Color Text = Color.Parse("#F1F3F7");

    public static void Set(string key, double percent) {
        if (Application.Current is not { } app) return;
        var t = Math.Clamp(percent / 100, 0, 1);
        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        app.Resources[key] = new SolidColorBrush(Color.FromRgb(Mix(Canvas.R, Text.R, t), Mix(Canvas.G, Text.G, t), Mix(Canvas.B, Text.B, t)));
    }
}
