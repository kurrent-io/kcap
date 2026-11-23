using Avalonia;

namespace Capacitor.App.Views;

/// The type scale defined in App.axaml, for controls built in code.
internal static class TypeScale {
    public static double Caption => Size("KcapFontSizeCaption");
    public static double Small => Size("KcapFontSizeSmall");
    public static double Body => Size("KcapFontSizeBody");
    public static double Large => Size("KcapFontSizeLarge");
    public static double Heading => Size("KcapFontSizeHeading");
    public static double Title => Size("KcapFontSizeTitle");
    public static double Display => Size("KcapFontSizeDisplay");

    static double Size(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true && value is double size
            ? size : throw new InvalidOperationException($"Missing type scale resource: {key}");
}
