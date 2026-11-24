namespace Capacitor.App;

internal sealed record DesktopAppearancePreferences(
    DesktopDensity Density = DesktopDensity.Comfortable,
    DesktopReadingFont ReadingFont = DesktopReadingFont.Inter,
    bool HighContrast = false,
    DesktopMotion Motion = DesktopMotion.System,
    DesktopVisualMode VisualMode = DesktopVisualMode.Classic) {

    public static DesktopAppearancePreferences Read(IReadOnlyDictionary<string, string>? values) => new(
        values?.GetValueOrDefault("density") == "compact" ? DesktopDensity.Compact : DesktopDensity.Comfortable,
        values?.GetValueOrDefault("reading_font") == "monospace" ? DesktopReadingFont.Monospace : DesktopReadingFont.Inter,
        values?.GetValueOrDefault("high_contrast") == "true",
        values?.GetValueOrDefault("motion") == "reduce" ? DesktopMotion.Reduce : DesktopMotion.System,
        values?.GetValueOrDefault("visual_mode") == "modern" ? DesktopVisualMode.Modern : DesktopVisualMode.Classic);
}
