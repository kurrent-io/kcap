using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public static class TrayIconRenderer {
    const int IconSize = 32;
    const int OverlayDiameter = 12; // bottom-right state overlay
    const double OverlayMargin = 1;
    const int MaxDigitCount = 9; // above this, CountBadge collapses to "9+"
    const int CountCap = 10;     // cache-key clamp: every count >= this renders the same "9+" badge

    const string BadgeBackgroundHex = "#173F36"; // KcapSuccessDim — legible under the white numeral

    static readonly IBrush BadgeText = Brushes.White;
    static readonly IBrush BadgeBackground = new SolidColorBrush(Color.Parse(BadgeBackgroundHex));
    static readonly IBrush PreviewBackground = new SolidColorBrush(Color.Parse("#292B2D"));
    static readonly Dictionary<(TrayState State, int CappedCount, StatusGlyphKind? Glyph), WindowIcon> Cache = new();

    public static WindowIcon Get(TrayState state, int count) => Get(state, count, null);

    internal static WindowIcon Get(TrayState state, int count, StatusGlyphKind? glyph) {
        var key = (state, CappedCount: state == TrayState.Running ? Math.Clamp(count, 0, CountCap) : 0, glyph);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var icon = Render(state, key.CappedCount, glyph);
        Cache[key] = icon;
        return icon;
    }

    internal static string CountBadge(int count) => count switch {
        <= 0 => "0",
        <= MaxDigitCount => count.ToString(CultureInfo.InvariantCulture),
        _ => "9+",
    };

    static WindowIcon Render(TrayState state, int cappedCount, StatusGlyphKind? glyph) {
        var bitmap = new RenderTargetBitmap(new PixelSize(IconSize, IconSize));
        using (var context = bitmap.CreateDrawingContext()) {
            context.DrawImage(ProductIcon.TrayBitmap, new Rect(0, 0, IconSize, IconSize));
            if (state == TrayState.Running) DrawCountBadge(context, cappedCount, glyph is not null);
            else if (glyph is { } kind) DrawStatusGlyph(context, kind);
            else DrawStatusDot(context, state);
        }
        return new WindowIcon(bitmap);
    }

    static void DrawStatusDot(DrawingContext context, TrayState state) {
        var brush = new SolidColorBrush(Color.Parse(StatusHexFor(state)));
        var (center, radius) = OverlayCircle();
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    static void DrawStatusGlyph(DrawingContext context, StatusGlyphKind kind) {
        var (center, radius) = OverlayCircle();
        var ink = kind switch {
            StatusGlyphKind.Healthy => StatusColors.Connected,
            StatusGlyphKind.Attention => StatusColors.InProgress,
            StatusGlyphKind.Failure => StatusColors.Disrupted,
            _ => "#E2E2E0",
        };
        context.DrawEllipse(PreviewBackground, null, center, radius + 1, radius + 1);
        var icon = new AppIcon { Kind = StatusGlyph.Appearance(kind).Icon, Size = OverlayDiameter,
            Foreground = new SolidColorBrush(Color.Parse(ink)) };
        var size = new Size(OverlayDiameter, OverlayDiameter);
        icon.Measure(size);
        icon.Arrange(new Rect(size));
        using var glyph = new RenderTargetBitmap(new PixelSize(OverlayDiameter, OverlayDiameter));
        glyph.Render(icon);
        context.DrawImage(glyph, new Rect(center - new Point(radius, radius), size));
        if (kind == StatusGlyphKind.Activity) context.DrawEllipse(icon.Foreground, null, center, 2, 2);
    }

    static void DrawCountBadge(DrawingContext context, int cappedCount, bool preview) {
        var (center, radius) = OverlayCircle();
        context.DrawEllipse(preview ? PreviewBackground : BadgeBackground, null, center, radius, radius);

        var text = new FormattedText(
            CountBadge(cappedCount), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 8, BadgeText);
        context.DrawText(text, center - new Point(text.Width / 2, text.Height / 2));
    }

    static (Point Center, double Radius) OverlayCircle() {
        const double radius = OverlayDiameter / 2.0;
        var center = new Point(IconSize - radius - OverlayMargin, IconSize - radius - OverlayMargin);
        return (center, radius);
    }

    static string StatusHexFor(TrayState state) => state switch {
        TrayState.Stopped    => StatusColors.Unavailable,
        TrayState.Connecting => StatusColors.InProgress,
        TrayState.Idle       => StatusColors.Connected,
        _                    => StatusColors.Disrupted,
    };
}
