namespace Capacitor.App;

/// The status palette colours as hex, for code that runs without the app's resources (the tray
/// icon) and for tones compared by value. StatusColorsTests holds each equal to its App.axaml brush.
/// Plain strings: a caller that caches must use an immutable brush.
public static class StatusColors {
    public const string Connected   = "#5BE0B3";
    public const string InProgress  = "#F4B860";
    public const string Disrupted   = "#FF7272";
    public const string Unavailable = "#949BAA";
    /// Work in progress that needs nothing, in the launcher spinner's muted grey.
    public const string Pending     = "#B0B7C6";
}
