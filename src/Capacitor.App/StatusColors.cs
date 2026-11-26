namespace Capacitor.App;

/// The status palette colours as hex, for code that runs without the app's resources (the tray
/// icon) and for tones compared by value. StatusColorsTests holds each equal to its color in the Dark theme.
/// Plain strings: a caller that caches must use an immutable brush.
public static class StatusColors {
    public const string Connected   = "#64D998";
    public const string InProgress  = "#EBB64F";
    public const string Disrupted   = "#FF9087";
    public const string Unavailable = "#B0B1B0";
    /// Work in progress that needs nothing, in the launcher spinner's muted grey.
    public const string Pending     = "#BDBEBD";
}
