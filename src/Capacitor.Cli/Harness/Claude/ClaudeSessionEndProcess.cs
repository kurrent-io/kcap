using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>The Claude process a SessionEnd hook ran under. The hook's detached continuation can outlive that process, so
/// the hook reads it before the hand-off and passes it on.</summary>
static class ClaudeSessionEndProcess {
    public const string EnvVar = "KCAP_SESSION_END_PROCESS";

    // Read once per process, and cleared as it is read: what the continuation starts would otherwise take this process for
    // its own.
    static readonly Lazy<(bool Passed, ProcessIncarnation? Process)> FromHandOff =
        new(() => Take(() => Environment.GetEnvironmentVariable(EnvVar), () => Environment.SetEnvironmentVariable(EnvVar, null)));

    /// <summary>The process the hand-off passed on, or else the Claude process above this one. A value that doesn't read
    /// back is no process: this is the continuation, and the process above it is no longer Claude.</summary>
    public static ProcessIncarnation? Current() => FromHandOff.Value is (true, var process) ? process : Live();

    /// <summary>Takes what the hand-off passed out of the environment, ahead of anything the continuation starts.</summary>
    public static void TakeFromEnvironment() => _ = FromHandOff.Value;

    public static ProcessIncarnation? Live() => ProcessIncarnation.Of(ProcessHelpers.GetCodingAgentPid("claude", allowFallback: false) ?? 0);

    /// <summary>What the hand-off passed, clearing it once read.</summary>
    internal static (bool Passed, ProcessIncarnation? Process) Take(Func<string?> read, Action clear) {
        if (read() is not { } passed) return (false, null);

        clear();

        return (true, Decode(passed));
    }

    public static string Encode(ProcessIncarnation process) => $"{process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{process.StartToken}";

    // The token holds colons of its own, so only the first one splits.
    public static ProcessIncarnation? Decode(string text) =>
        text.IndexOf(':') is var split and > 0
     && int.TryParse(text.AsSpan(0, split), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 1
     && split < text.Length - 1
            ? new ProcessIncarnation(pid, text[(split + 1)..])
            : null;
}
