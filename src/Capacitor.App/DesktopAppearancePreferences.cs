namespace Capacitor.App;

internal sealed record DesktopAppearancePreferences(
    DesktopDensity Density = DesktopDensity.Comfortable,
    DesktopReadingFont ReadingFont = DesktopReadingFont.Inter,
    bool HighContrast = false) {

    public static DesktopAppearancePreferences Read(IReadOnlyDictionary<string, string>? values) => new(
        values?.GetValueOrDefault("density") == "compact" ? DesktopDensity.Compact : DesktopDensity.Comfortable,
        values?.GetValueOrDefault("reading_font") == "monospace" ? DesktopReadingFont.Monospace : DesktopReadingFont.Inter,
        values?.GetValueOrDefault("high_contrast") == "true");
}
