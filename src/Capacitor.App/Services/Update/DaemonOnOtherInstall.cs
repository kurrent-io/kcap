namespace Capacitor.App.Services.Update;

/// A daemon older than the app that runs a binary this app's update did not replace, such as an npm install:
/// restarting it starts the same version again, so only updating that binary moves it on.
/// <param name="Version">The daemon's version as it reports it.</param>
/// <param name="Executable">The binary the daemon process runs.</param>
/// <param name="CopyFailure">Set when the binary is this app's own copy of its CLI, which the app could not
/// replace: why not. Fixing that and reopening the app moves the daemon on, not <c>kcap update</c>.</param>
public sealed record DaemonOnOtherInstall(string Version, string Executable, string? CopyFailure = null);
