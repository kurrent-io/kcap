namespace Capacitor.Cli.Daemon.Harness.OpenCode;

/// <summary>One reviewer launch's private, empty directories. OpenCode loads global config from each of them.</summary>
internal sealed record OpenCodeReviewerDirs(string ConfigDir, string Home, string ConfigHome);
