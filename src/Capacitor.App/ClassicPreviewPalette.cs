using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Capacitor.App;

internal sealed class ClassicPreviewPalette(Application application) {
    const string ContextDividerThickness = "KcapContextDividerThickness";
    const string WorkspaceSelectionWeight = "KcapWorkspaceSelectionWeight";

    readonly Dictionary<SolidColorBrush, Color> _original = new();
    ThemeVariant? _originalVariant;

    ClassicAppearance _theme;
    bool _higherContrast;

    public void SetHigherContrast(bool enabled) {
        if (_higherContrast == enabled) return;
        _higherContrast = enabled;
        TryApply(_theme);
    }

    public bool TryApply(ClassicAppearance theme) {
        if (theme == ClassicAppearance.Original) {
            _theme = theme;
            if (_original.Count == 0) return true;
            foreach (var (brush, color) in _original) brush.Color = color;
            application.RequestedThemeVariant = _originalVariant;
            application.Resources.Remove(ContextDividerThickness);
            application.Resources.Remove(WorkspaceSelectionWeight);
            _original.Clear();
            return true;
        }

        var light = theme == ClassicAppearance.Light;
        var name = light ? "KcapClassicLight" : "KcapClassicDark";
        if (!application.TryFindResource(name, out var value) || value is not ResourceDictionary { Count: > 0 } palette) return false;
        var colors = new List<(SolidColorBrush Brush, Color Color)>(palette.Count);
        foreach (var key in palette.Keys) {
            if (!application.TryFindResource(key, out var target) || target is not SolidColorBrush brush || palette[key] is not Color color)
                return false;
            colors.Add((brush, _higherContrast ? ContrastColor(key, color, palette) : color));
        }
        // StaticResource consumers, including already-open flyouts, hold the original brush instance.
        if (_original.Count == 0) {
            _originalVariant = application.RequestedThemeVariant;
            application.Resources[ContextDividerThickness] = new Thickness(0);
            application.Resources[WorkspaceSelectionWeight] = FontWeight.SemiBold;
        }
        foreach (var (brush, color) in colors) {
            _original.TryAdd(brush, brush.Color);
            brush.Color = color;
        }
        _theme = theme;
        application.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        return true;
    }
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
