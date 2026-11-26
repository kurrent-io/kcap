using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Capacitor.App;

/// Recolors the shared palette brushes in place from <c>Design/Themes.axaml</c>, so every control,
/// open flyout and dialog already holding one follows a theme change, and selects the matching
/// Fluent variant.
internal sealed class DesktopPalette(Application application) {
    internal const string LightTheme = "KcapLightTheme";
    internal const string DarkTheme = "KcapDarkTheme";

    DesktopTheme? _theme;
    bool _higherContrast;

    public void SetHigherContrast(bool enabled) {
        if (_higherContrast == enabled) return;
        _higherContrast = enabled;
        if (_theme is { } theme) TryApply(theme);
    }

    /// <returns>False, with nothing changed, when the theme's dictionary is missing an entry.</returns>
    public bool TryApply(DesktopTheme theme) {
        var light = theme == DesktopTheme.Light;
        if (!application.TryFindResource(light ? LightTheme : DarkTheme, out var value) || value is not ResourceDictionary { Count: > 0 } palette)
            return false;
        // Higher contrast mixes these into other entries.
        if (ContrastSources.Any(key => !palette.TryGetValue(key, out var source) || source is not Color)) return false;
        var colors = new List<(SolidColorBrush Brush, Color Color)>(palette.Count);
        foreach (var key in palette.Keys) {
            if (!application.TryFindResource(key, out var target) || target is not SolidColorBrush brush || palette[key] is not Color color)
                return false;
            colors.Add((brush, _higherContrast ? ContrastColor(key, color, palette) : color));
        }
        // StaticResource consumers, including already-open flyouts, hold the shared brush instance.
        foreach (var (brush, color) in colors) brush.Color = color;
        _theme = theme;
        application.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        return true;
    }

    static readonly string[] ContrastSources =
        ["KcapTextBrush", "KcapMutedBrush", "KcapFaintBrush", "KcapFieldBorderBrush", "KcapSurfaceRaisedBrush"];

    static Color ContrastColor(object key, Color color, ResourceDictionary palette) => key switch {
        "KcapMutedBrush" or "KcapFaintBrush" or "KcapPlaceholderBrush"
            => Between(color, (Color)palette["KcapTextBrush"]!),
        "ProgressiveDisclosureDetailFadeBrush"
            => Between((Color)palette["KcapMutedBrush"]!, (Color)palette["KcapTextBrush"]!),
        "ProgressiveDisclosureFootnoteFadeBrush"
            => Between((Color)palette["KcapFaintBrush"]!, (Color)palette["KcapTextBrush"]!),
        "KcapBorderBrush" or "KcapBorderStrongBrush" => Between(color, (Color)palette["KcapFieldBorderBrush"]!),
        "KcapChoiceBorderBrush" or "KcapFieldBorderBrush" or "KcapTopicRailBrush" => (Color)palette["KcapTextBrush"]!,
        "ProgressiveDisclosureDetailCodeFadeBrush" or "ProgressiveDisclosureFootnoteCodeFadeBrush"
            => (Color)palette["KcapSurfaceRaisedBrush"]!,
        _ => color,
    };

    static Color Between(Color a, Color b) => Color.FromRgb(
        (byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
}
