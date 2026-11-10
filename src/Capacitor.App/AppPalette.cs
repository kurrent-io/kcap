using Avalonia;
using Avalonia.Media;

namespace Capacitor.App;

internal static class AppPalette {
    public static IBrush Brush(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush
            ? brush : throw new InvalidOperationException($"Missing app palette resource: {key}");
}

/// Semantic palette keys for status presentation.
internal static class AppStatusPalette {
    public const string Connected   = "KcapSuccessBrush";
    public const string Execution   = "KcapPurpleBrush";
    public const string Attention   = "KcapWarningBrush";
    public const string Disrupted   = "KcapDangerBrush";
    public const string Unavailable = "KcapFaintBrush";
}
