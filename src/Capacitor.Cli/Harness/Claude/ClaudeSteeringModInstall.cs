namespace Capacitor.Cli.Harness.Claude;

/// <summary>What putting the steering mod into Claude Code's settings did: a line saying it went in, or why it
/// did not. Both are null when there was nothing to do.</summary>
internal readonly record struct ClaudeSteeringModInstall(string? Installed, string? Failed);
