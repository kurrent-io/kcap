namespace Capacitor.App.Platform.Linux;

/// The app could not copy its CLI out of the AppImage, so the copy an earlier run left keeps its old version.
/// <param name="Directory">Where the copy lives.</param>
/// <param name="Reason">Why it could not be replaced.</param>
public sealed record CliStagingFailure(string Directory, string Reason);
