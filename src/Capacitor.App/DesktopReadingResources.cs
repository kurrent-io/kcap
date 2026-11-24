using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App;

internal sealed class DesktopReadingResources(Application application) {
    internal const string SplineFamily = "avares://kcap-desktop/Assets/SplineSansMono#Spline Sans Mono";
    internal const string MonospaceReadingFamily = SplineFamily + ",Menlo,Consolas,DejaVu Sans Mono,monospace,fonts:Inter#Inter";
    int _layout;
    int _revision;
    double _zoom = 1;

    public void SetZoom(double zoom) {
        if (_zoom.Equals(zoom)) return;
        _zoom = zoom;
        if (_layout != 0) PublishLayout();
    }

    public void Apply(DesktopAppearancePreferences? preferences) {
        var compact = preferences?.Density == DesktopDensity.Compact;
        var mono = preferences?.ReadingFont == DesktopReadingFont.Monospace;
        var layout = preferences is null ? 0 : 1 + (compact ? 1 : 0) + (mono ? 2 : 0);
        if (layout == _layout) return;
        _layout = layout;
        var resources = application.Resources;
        Set(resources, "KcapChatCardPadding", compact ? new Thickness(11, 8) : null);
        Set(resources, "KcapActionPadding", compact ? new Thickness(13, 6) : null);
        Set(resources, "KcapChoicePadding", compact ? new Thickness(11, 6) : null);
        Set(resources, "KcapTranscriptGap", compact ? new Thickness(0, 6, 0, 9) : null);
        Set(resources, "KcapUserTurnPadding", compact ? new Thickness(11, 8) : null);
        Set(resources, "KcapComposerPadding", compact ? new Thickness(10, 6, 10, 9) : null);
        Set(resources, "KcapSessionRowPadding", compact ? new Thickness(6, 4) : null);
        Set(resources, "KcapProseLineHeight", compact ? 22d : null);
        Set(resources, "KcapReadingFamily", mono ? new FontFamily(MonospaceReadingFamily) : null);
        Set(resources, "KcapUseMonospaceReading", mono ? true : null);
        Set(resources, "KcapInlineMessageChrome", preferences is null ? null : true);
        PublishLayout();
    }

    void PublishLayout() {
        Set(application.Resources, "KcapReadingLayout", _layout == 0 ? null : ++_revision);
    }

    static void Set(IResourceDictionary resources, string key, object? value) {
        if (value is null) resources.Remove(key);
        else if (!resources.TryGetValue(key, out var old) || !Equals(old, value)) resources[key] = value;
    }
}
