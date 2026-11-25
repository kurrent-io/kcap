using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels;

public static partial class HandoffFailureText {
    public const string StartChanged =
        "The branch changed after it was checked, so nothing was started. Check the new starting point and start again.";
    public const string SourceUnavailable =
        "The original session is no longer available for handoff. Check that you can still open it, or start a new session.";
    public const string InvalidHandoff =
        "Your local kcap daemon couldn't start this hand-off, so nothing was started. Update kcap and try again.";
    public const string RepoMismatch =
        "The checkout on this machine is for a different repository. Choose a checkout of the original repository and try again.";
    public const string RepoNotAllowed =
        "The local daemon isn't allowed to use that checkout. Choose an allowed checkout or update the machine's allowed repositories.";
    public const string NotOwner = "Only this machine's owner can hand off a session to it. Choose a machine you own.";
    public const string Generic = "Couldn't start the hand-off. Try again.";

    public static string For(string? reason) {
        if (string.IsNullOrWhiteSpace(reason)) return Generic;
        var match = CodePrefix().Match(reason);
        var code = match.Success ? match.Groups["code"].Value : null;
        switch (code) {
            case "handoff_start_changed": return StartChanged;
            case "handoff_source_unavailable": return SourceUnavailable;
            case "invalid_handoff": return InvalidHandoff;
            case "handoff_unsupported_daemon": return HandoffComposerViewModel.DaemonTooOld;
            case "handoff_not_owner": return NotOwner;
            case Cli.Core.LocalIpc.HandoffRefusals.RepoMismatch: return RepoMismatch;
            case Cli.Core.LocalIpc.HandoffRefusals.RepoNotAllowed: return RepoNotAllowed;
        }
        return HomeViewModel.FriendlyLaunchFailure(reason, Generic);
    }

    /// A failed resume, worded as the fallback the composer then offers.
    public static string ResumeFallback(string? reason) =>
        $"Couldn't resume this session here ({ResumeReason(reason)}). Hand off starts a new session with its context.";

    /// Whether the resume was attempted and refused, by the server, the daemon or its owner, rather than failing to
    /// reach the server at all.
    public static bool IsResumeRefusal(string? reason) =>
        reason is not null && CodePrefix().Match(reason) is { Success: true } match
     && (match.Groups["code"].Value.StartsWith("resume_", StringComparison.Ordinal) || match.Groups["code"].Value == OwnerDenied);

    const string OwnerDenied = "launch_denied_by_owner";

    static string ResumeReason(string? reason) {
        if (string.IsNullOrWhiteSpace(reason)) return "it didn't start";
        var match = CodePrefix().Match(reason);
        return (match.Success ? match.Groups["code"].Value : null) switch {
            "resume_source_active"         => "it's active again",
            "resume_source_reserved"       => "it's being resumed or handed off elsewhere",
            "resume_unsupported_daemon"    => "your local kcap daemon can't resume sessions yet",
            "resume_directory_missing"     => "its directory is gone",
            "resume_directory_not_allowed" => "your local kcap daemon isn't allowed to work in its directory",
            "resume_failed"                => "the agent couldn't pick it up",
            "resume_other_machine" or "resume_not_resumable" or "resume_store_changed" or "resume_unsupported_vendor"
             or "resume_invalid" or "resume_not_owner" or "resume_source_unavailable" => "what it ran under isn't available here",
            OwnerDenied => "that machine's consent policy refused the launch",
            _ => "the agent couldn't start",
        };
    }

    // SignalR delivers a hub's refusal behind its own "… on the server. HubException: " lead.
    [GeneratedRegex("(?:^|HubException: )(?<code>[a-z][a-z0-9]*(?:_[a-z0-9]+)+)(?::|$)")]
    private static partial Regex CodePrefix();
}
