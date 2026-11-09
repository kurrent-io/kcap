namespace Capacitor.App.Services.CliAdoption;

/// <summary>What launchd holds under a daemon's label. <paramref name="Program"/> is what it loaded, which can differ
/// from what the plist says now.</summary>
internal sealed record LaunchdJob(bool Loaded, int? Pid, string? Program);
