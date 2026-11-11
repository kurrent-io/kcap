using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

internal enum AntigravityReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// The reviewer flag is an opt-out and applies only to cross-principal review launches.
/// Platform and build checks apply to hosted launches too: both depend on the isolated HOME
/// suppressing global config and capture hooks. Losing that behavior can duplicate ingestion.
/// The persisted, operator-affirmed version is a minimum: upgrades are admitted; raising the floor
/// excludes a build with broken containment. Dotfiles must not substitute for that affirmation.
/// The factory adds binary presence and shares this ladder between advertisement and launch.
/// </summary>
internal static class AntigravityReviewerCapability {
    /// <summary>
    /// The decision, with the platform passed IN rather than read from the ambient OS, so every arm is
    /// reachable from any host. There is deliberately no ambient-reading overload: the one production
    /// caller holds a platform seam of its own. Reading the OS in here would short-circuit every
    /// consent and version test to <see cref="AntigravityReviewerDecision.UnsupportedPlatform"/> on a
    /// Windows test host.
    /// </summary>
    internal static AntigravityReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Consent FIRST and short-circuiting: an installed-but-wedged agy must not be probed — let
        // alone stall a daemon start — for a feature the operator switched off.
        if (!operatorEnabled) return AntigravityReviewerDecision.Disabled;

        // The Windows home is owner-only and agy's profile variables point into it, but no real agy run
        // has confirmed that agy writes nothing outside it there. Containment is proven by a real
        // process run, never assumed, so Windows stays refused until one does.
        if (!posixHost) return AntigravityReviewerDecision.UnsupportedPlatform;

        // No discard: `_ => Allowed` would silently ADMIT any arm added later, which is the wrong
        // direction for this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => AntigravityReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => AntigravityReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => AntigravityReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => AntigravityReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => AntigravityReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two cannot
    /// disagree about WHY a launch was denied.
    /// </summary>
    /// <param name="binaryPath">The binary the daemon would launch, named in the unresolved arm so an
    /// operator checks the right one rather than whatever is first on PATH.</param>
    internal static string DenialReason(
            AntigravityReviewerDecision decision, string? installedVersion, string? minimumVersion,
            string binaryPath) =>
        decision switch {
            AntigravityReviewerDecision.Disabled =>
                "antigravity_unattended_reviewer_disabled: this daemon has EXPLICITLY disabled "
              + "unattended Antigravity reviews. Unset KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER in the "
              + "daemon's environment (not on the server) to restore the default, which is enabled — or "
              + "set it to 1. A review runs under this daemon user's authority either way, as it does "
              + "for the never-gated Claude, Codex, Cursor and Copilot reviewers.",

            AntigravityReviewerDecision.UnsupportedPlatform =>
                "antigravity_reviewer_unsupported_platform: Antigravity is not offered on Windows yet. "
              + "A launch runs agy under an owner-only per-launch home that holds its conversation "
              + "transcript, and no real agy run on Windows has yet confirmed that agy writes nothing "
              + "outside that home.",

            AntigravityReviewerDecision.VersionUnresolved =>
                $"antigravity_reviewer_version_unresolved: the version of '{binaryPath}' could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. Check "
              + "that the Antigravity CLI is installed (the `agy` binary — the IDE alone is not "
              + $"enough), that `agy --version` succeeds, and set {HarnessId.Antigravity.PathEnvVar} if it "
              + "lives elsewhere. A build we cannot identify is refused rather than assumed compatible.",

            AntigravityReviewerDecision.VersionNoMinimum =>
                "antigravity_reviewer_version_no_minimum: this daemon has no recorded minimum agy "
              + "version, so there is nothing to check the installed build against. A daemon records "
              + "one at startup whenever the Antigravity CLI resolves, so the usual cause is a daemon "
              + "that started before `agy` was installed — restart it, or record the installed build "
              + "now with `kcap daemon reviewer affirm --vendor antigravity`.",

            AntigravityReviewerDecision.VersionIncomparable =>
                $"antigravity_reviewer_version_incomparable: agy {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor antigravity`.",

            // Exhaustive, like Decide's switch: a discard would print the below-minimum text for a
            // future arm meaning something else, sending an operator to the wrong fix. Allowed throws
            // because asking for the denial reason of a permitted decision is a caller bug.
            AntigravityReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            AntigravityReviewerDecision.VersionBelowMinimum =>
                $"antigravity_reviewer_version_below_minimum: agy {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. "
              + "Containment here depends on the build honouring HOME and reading no other global "
              + "config source — for a hosted agent as much as for a reviewer — so an OLDER build than "
              + "the one recorded is refused. Upgrade the Antigravity CLI, or deliberately lower the "
              + "minimum to the installed build with `kcap daemon reviewer affirm --vendor antigravity`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
