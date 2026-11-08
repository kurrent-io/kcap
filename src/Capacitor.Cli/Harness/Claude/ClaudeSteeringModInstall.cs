namespace Capacitor.Cli.Harness.Claude;

/// <summary>What putting the steering mod into Claude Code's settings did: lines to say, and failures, each of which
/// leaves steering unusable somewhere.</summary>
internal sealed record ClaudeSteeringModInstall(IReadOnlyList<string> Notes, IReadOnlyList<string> Failures) {
    public static ClaudeSteeringModInstall None { get; } = new([], []);
}
