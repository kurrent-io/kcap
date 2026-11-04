namespace Capacitor.App.Services.Update;

/// A daemon older than the app that runs a kcap this app does not ship, such as an npm install: restarting it
/// starts the same version again, so only updating that install moves it on.
/// <param name="Version">The daemon's version as it reports it.</param>
/// <param name="Executable">The binary the daemon process runs.</param>
public sealed record DaemonOnOtherInstall(string Version, string Executable);
