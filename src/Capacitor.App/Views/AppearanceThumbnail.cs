using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Capacitor.App.Views;

public sealed class AppearanceThumbnail : Control {
    public static readonly StyledProperty<DesktopColorScheme> SchemeProperty =
        AvaloniaProperty.Register<AppearanceThumbnail, DesktopColorScheme>(nameof(Scheme));

    public DesktopColorScheme Scheme {
        get => GetValue(SchemeProperty);
        set => SetValue(SchemeProperty, value);
    }

    static AppearanceThumbnail() => AffectsRender<AppearanceThumbnail>(SchemeProperty);

    sealed class Sample(ResourceDictionary palette) {
        public IBrush Canvas { get; } = Brush("KcapCanvasBrush", palette);
        public IBrush Rail { get; } = Brush("KcapSurfaceRaisedBrush", palette);
        public IBrush Ink { get; } = Brush("KcapMutedBrush", palette);
        public IBrush Subtle { get; } = Brush("KcapBorderStrongBrush", palette);
        public IBrush Selection { get; } = Brush("KcapSelectionBrush", palette);
        public IBrush Accent { get; } = Brush("KcapInfoBrush", palette);
        public IPen Edge { get; } = new ImmutablePen(Brush("KcapChoiceBorderBrush", palette), 0.75);

        static ImmutableSolidColorBrush Brush(string key, ResourceDictionary source) => new ImmutableSolidColorBrush((Color)source[key]!);
    }

    Sample? _light;
    Sample? _dark;

    public override void Render(DrawingContext context) {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using var transform = context.PushTransform(Matrix.CreateScale(Bounds.Width / 100, Bounds.Height / 62));
        using var clip = context.PushClip(new RoundedRect(new Rect(0, 0, 100, 62), 5));
        if (Scheme != DesktopColorScheme.Dark && this.TryFindResource("KcapClassicLight", out var light) && light is ResourceDictionary lightPalette)
            DrawWindow(context, _light ??= new(lightPalette));
        if (Scheme == DesktopColorScheme.Light || !this.TryFindResource("KcapClassicDark", out var dark) || dark is not ResourceDictionary darkPalette) return;
        using var half = context.PushClip(Scheme == DesktopColorScheme.Auto ? new Rect(50, 0, 50, 62) : new Rect(0, 0, 100, 62));
        DrawWindow(context, _dark ??= new(darkPalette));
    }

    static void DrawWindow(DrawingContext context, Sample colors) {
        context.FillRectangle(colors.Selection, new Rect(0, 0, 100, 62));
        context.DrawRectangle(colors.Rail, colors.Edge, new Rect(7, 10, 89, 57), 4, 4);
        for (var i = 0; i < 3; i++)
            context.DrawEllipse(colors.Subtle, null, new Point(13 + 4 * i, 15), 1, 1);
        context.FillRectangle(colors.Selection, new Rect(10, 24, 18, 5), 2);
        context.FillRectangle(colors.Accent, new Rect(13, 26, 10, 1), 0.5f);
        context.FillRectangle(colors.Subtle, new Rect(13, 33, 12, 2), 1);
        context.FillRectangle(colors.Subtle, new Rect(13, 39, 9, 2), 1);
        context.DrawRectangle(colors.Canvas, colors.Edge, new Rect(31, 19, 70, 49), 3, 3);
        context.FillRectangle(colors.Ink, new Rect(38, 26, 25, 3), 1);
        context.FillRectangle(colors.Subtle, new Rect(38, 35, 49, 2), 1);
        context.FillRectangle(colors.Subtle, new Rect(38, 40, 34, 2), 1);
        context.DrawRectangle(colors.Rail, colors.Edge, new Rect(38, 49, 54, 9), 2, 2);
    }
}
