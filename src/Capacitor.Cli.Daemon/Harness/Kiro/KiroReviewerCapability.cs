using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Kiro;

internal enum KiroReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Kiro's trusted fs_read is not path-scoped: reviews can read any file accessible to the daemon
/// user and return findings to a different requester. The reviewer switch is an operator opt-out.
/// Containment relies on Kiro honoring an isolated KIRO_HOME and on removing workspace config.
/// The recorded build floor admits upgrades; raise it to exclude a build with broken suppression.
/// </summary>
internal static class KiroReviewerCapability {
    /// <summary>Production entry point: reads the host platform, then defers to the pure overload.</summary>
    internal static KiroReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) =>
        Decide(!OperatingSystem.IsWindows(), operatorEnabled, installedVersion, minimumVersion);

    /// <summary>
    /// Take the platform as an input so consent and version branches remain reachable and testable
    /// from every host, including hosts whose ambient platform would refuse the launch.
    /// </summary>
    internal static KiroReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Windows has no 0700, so the transcript-bearing reviewer home cannot be made owner-only and
        // the disposal requirement cannot be met. Refuse rather than advertise a reviewer whose review
        // context is world-readable.
        if (!posixHost) return KiroReviewerDecision.UnsupportedPlatform;

        // Operator flag FIRST and short-circuiting. Evaluating the version probe alongside it would let
        // an installed-but-wedged vendor binary hang daemon startup on a feature the operator switched
        // off — the same trap the Gemini gate documents.
        if (!operatorEnabled) return KiroReviewerDecision.Disabled;

        // No discard: `_ => Allowed` silently ADMITTED any arm added later, which is the wrong
        // direction for this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => KiroReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => KiroReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => KiroReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => KiroReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => KiroReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide(bool, string?, string?)"/> so the two
    /// cannot disagree about WHY a launch was denied. The disabled text is the acceptance artifact
    /// for the risk in this type's summary, so its content is asserted, not just its presence.
    /// </summary>
    internal static string DenialReason(
            KiroReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            KiroReviewerDecision.UnsupportedPlatform =>
                "kiro_reviewer_unsupported_platform: the Kiro unattended reviewer is POSIX-only. Its "
              + "isolated home holds the reviewer's own transcript, and so the review context, and "
              + "cannot be created owner-only on this platform.",

            KiroReviewerDecision.Disabled =>
                "kiro_unattended_reviewer_disabled: this daemon has EXPLICITLY disabled Kiro as an "
              + "unattended review-flow reviewer. Unset KCAP_KIRO_UNATTENDED_REVIEWER in the daemon's "
              + "environment (not on the server) to restore the default, which is enabled — or set it "
              + "to 1. Note what the switch does and does not buy: a reviewer runs under this daemon "
              + "user's authority either way, and Claude, Codex, Cursor and Copilot are never gated, so "
              + "disabling Kiro alone does not stop a requester who can simply ask for one of those.",

            KiroReviewerDecision.VersionUnresolved =>
                "kiro_reviewer_version_unresolved: the installed kiro-cli version could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. A build "
              + "we cannot identify is refused rather than assumed compatible.",

            KiroReviewerDecision.VersionNoMinimum =>
                "kiro_reviewer_version_no_minimum: this daemon has no recorded minimum kiro-cli "
              + "version, so there is nothing to check the installed build against. A daemon records "
              + "one automatically at startup, so the usual cause is that the version probe failed "
              + "then — check that `kiro-cli --version` succeeds for the daemon user, and restart. To "
              + "record one now without restarting, run `kcap daemon reviewer affirm --vendor kiro`.",

            KiroReviewerDecision.VersionIncomparable =>
                $"kiro_reviewer_version_incomparable: kiro-cli {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor kiro`.",

            // Exhaustive, like Decide's switch: a discard would print the below-minimum text for a
            // future arm meaning something else, sending an operator to the wrong fix. Allowed throws
            // because asking for the denial reason of a permitted decision is a caller bug.
            KiroReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            KiroReviewerDecision.VersionBelowMinimum =>
                $"kiro_reviewer_version_below_minimum: kiro-cli {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. The "
              + "reviewer's containment depends on the build honouring KIRO_HOME and reading no other "
              + "global config source, so an OLDER build than the one recorded is refused. Upgrade "
              + "kiro-cli, or deliberately lower the minimum to the installed build with "
              + "`kcap daemon reviewer affirm --vendor kiro`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
