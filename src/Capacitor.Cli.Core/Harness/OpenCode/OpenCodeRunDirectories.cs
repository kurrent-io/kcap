namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>The private, initially empty directories one headless OpenCode run is pointed at in place of the
/// user's.</summary>
sealed record OpenCodeRunDirectories(string ConfigHome, string ConfigDir, string DataHome, string StateHome);
