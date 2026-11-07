using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>The Claude process a SessionEnd hook ran under. The hook's detached continuation can outlive that process, so
/// the hook reads it before the hand-off and passes it on.</summary>
static class ClaudeSessionEndProcess {
    public const string EnvVar = "KCAP_SESSION_END_PROCESS";

    /// <summary>The process the hand-off passed on, or else the Claude process above this one. A value that doesn't read
    /// back is no process: this is the continuation, and the process above it is no longer Claude.</summary>
    public static ProcessIncarnation? Current() => Environment.GetEnvironmentVariable(EnvVar) is { } passed ? Decode(passed) : Live();

    public static ProcessIncarnation? Live() => ProcessIncarnation.Of(ProcessHelpers.GetCodingAgentPid("claude", allowFallback: false) ?? 0);

    public static string Encode(ProcessIncarnation process) => $"{process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{process.StartToken}";

    // The token holds colons of its own, so only the first one splits.
    public static ProcessIncarnation? Decode(string text) =>
        text.IndexOf(':') is var split and > 0
     && int.TryParse(text.AsSpan(0, split), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 1
     && split < text.Length - 1
            ? new ProcessIncarnation(pid, text[(split + 1)..])
            : null;
}
