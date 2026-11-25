using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels;

public static partial class InterruptionReasons {
    static readonly FrozenDictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["auto_resume_off"] = "Automatic resume is turned off",
        ["too_old"] = "It was idle too long to resume",
        ["predecessor_process_unresolved"] = "The previous daemon's process for it could not be confirmed stopped",
        ["worktree_missing"] = "Its worktree no longer exists",
        ["native_resume_unsupported"] = "Its coding tool cannot resume a session",
        ["stopped"] = "It was stopped while resuming",
        ["acp_binding_unavailable"] = "Its transcript could not be reattached",
        ["registration_refused"] = "The server refused to register it again",
        ["run_ended"] = "Its run had already ended",
        ["superseded"] = "A newer session took its place",
        ["source_reserved"] = "Its session is in use elsewhere",
        ["source_lease_unreachable"] = "The machine holding its session could not be reached",
        ["source_lease_refused"] = "The machine holding its session refused to hand it over",
        ["source_lease_lost"] = "It lost its hold on the session",
        ["runtime_unstopped"] = "Its previous process could not be stopped",
        ["daemon_renaming"] = "The daemon was being renamed",
        ["daemon_capacity"] = "The daemon was at its agent limit",
        ["launch_rejected"] = "The relaunch was refused",
        ["runtime_exited"] = "It exited as soon as it started",
        ["launch_cancelled"] = "Its relaunch was cancelled",
        ["daemon_shutdown"] = "The daemon shut down while resuming it",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IEnumerable<string> KnownCodes => Sentences.Keys;

    public static string? Describe(string? reason) {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.StartsWith("tombstone:", StringComparison.Ordinal) ? reason["tombstone:".Length..] : reason;
        var match = ExitCode().Match(trimmed);
        var code = match.Success ? trimmed[..match.Index] : trimmed;
        if (!Sentences.TryGetValue(code, out var sentence)) return null;
        return match.Success ? $"{sentence} (exit code {match.Groups[1].Value})" : sentence;
    }

    [GeneratedRegex(@" \(exit code (-?\d+)\)$")]
    private static partial Regex ExitCode();
}
