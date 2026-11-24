using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App;

internal sealed class DesktopModernMaterials(Application application) {
    internal const string FillResource = "KcapModernOverlayBrush";
    internal const string ShadowResource = "KcapModernOverlayShadow";
    static readonly BoxShadows Shadow = BoxShadows.Parse("0 4 8 0 #38000000");
    Task? _graphicsProbe;
    ICompositionGpuInterop? _graphics;
    bool _allowShadow;

    internal void Apply(DesktopVisualMode mode, bool highContrast) {
        _allowShadow = mode == DesktopVisualMode.Modern && !highContrast && AllowsShadow();
        if (_allowShadow) _graphicsProbe ??= ProbeGraphicsAsync();
        Apply(mode, highContrast, _allowShadow && _graphics is { IsLost: false });
    }

    async Task ProbeGraphicsAsync() {
        if (Compositor.TryGetDefaultCompositor() is not { } compositor) return;
        try {
            _graphics = await compositor.TryGetCompositionGpuInterop();
        } catch (InvalidOperationException) {
            return;
        }
        if (_allowShadow && _graphics is { IsLost: false })
            Apply(DesktopVisualMode.Modern, highContrast: false, supportsShadow: true);
    }

    internal void Apply(DesktopVisualMode mode, bool highContrast, bool supportsShadow) {
        var modern = mode == DesktopVisualMode.Modern;
        var tint = modern && !highContrast && supportsShadow;
        var fill = modern && application.TryFindResource(tint ? "KcapModernTintBrush" : "KcapSurfaceRaisedBrush", out var brush)
            ? brush : null;
        Set(FillResource, fill);
        Set(ShadowResource, tint ? Shadow : null);
    }

    void Set(string key, object? value) {
        var resources = application.Resources;
        if (value is null) resources.Remove(key);
        else if (!resources.TryGetValue(key, out var old) || !Equals(old, value)) resources[key] = value;
    }

    static bool AllowsShadow() {
        if (!OperatingSystem.IsMacOS()) return false;

        var workspace = AppKitInterop.Send(AppKitInterop.GetClass("NSWorkspace"), AppKitInterop.Selector("sharedWorkspace"));
        return Allows(workspace, "accessibilityDisplayShouldReduceTransparency")
            && Allows(workspace, "accessibilityDisplayShouldIncreaseContrast");
    }

    static bool Allows(nint workspace, string preference) {
        var selector = AppKitInterop.Selector(preference);
        return (AppKitInterop.Send(workspace, AppKitInterop.Selector("respondsToSelector:"), selector) & 0xff) != 0
            && (AppKitInterop.Send(workspace, selector) & 0xff) == 0;
    }
}
