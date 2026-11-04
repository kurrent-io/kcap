namespace Capacitor.App.Platform.Linux;

/// The app could not copy its CLI out of the AppImage, so the copy an earlier run left keeps its old version.
/// <param name="Directory">Where the copy lives.</param>
/// <param name="Reason">Why it could not be replaced.</param>
/// <param name="DaemonCopied">The copy's daemon binary was replaced before the failure, so a daemon there restarts into
/// this version.</param>
public sealed record CliStagingFailure(string Directory, string Reason, bool DaemonCopied);
