using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Capacitor.App;

internal sealed class ClassicPreviewPalette(Application application) {
    const string ContextDividerThickness = "KcapContextDividerThickness";

    readonly Dictionary<SolidColorBrush, Color> _original = new();
    ThemeVariant? _originalVariant;

    public bool TryApply(ClassicAppearance theme) {
        if (theme == ClassicAppearance.Original) {
            if (_original.Count == 0) return true;
            foreach (var (brush, color) in _original) brush.Color = color;
            application.RequestedThemeVariant = _originalVariant;
            application.Resources.Remove(ContextDividerThickness);
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
            colors.Add((brush, color));
        }
        // StaticResource consumers, including already-open flyouts, hold the original brush instance.
        if (_original.Count == 0) {
            _originalVariant = application.RequestedThemeVariant;
            application.Resources[ContextDividerThickness] = new Thickness(0);
        }
        foreach (var (brush, color) in colors) {
            _original.TryAdd(brush, brush.Color);
            brush.Color = color;
        }
        application.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        return true;
    }
}
