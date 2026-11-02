namespace Capacitor.Cli.Core.Config;

/// <param name="AppliesLive">True when a change takes effect for the next launch without a daemon restart.</param>
/// <param name="DesktopAppOnly">Read only by the desktop app: the daemon never reports it, so its saved switch is its whole state.</param>
/// <param name="OnByDefault">What a profile that never set the switch gets on this platform.</param>
public sealed record ExperimentalFeature(
    string Id, string DisplayName, string Description, bool AppliesLive, bool DesktopAppOnly = false, bool OnByDefault = false);
