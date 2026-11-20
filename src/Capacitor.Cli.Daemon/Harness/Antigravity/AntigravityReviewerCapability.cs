using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

internal enum AntigravityReviewerDecision {
    Allowed,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether this daemon may run Antigravity's CLI (<c>agy</c>) as an unattended review-flow reviewer:
/// consent, then build minimum. Pure, so every arm is testable without a process.
/// <para>Enabled by default; the switch is an opt-out. Only the disabled arm is reviewer-only: the risk
/// is cross-principal, and a hosted launch is always the daemon owner's.</para>
/// <para>Containment is an empty per-launch <see cref="AntigravityReviewerHome"/>, which works only if
/// the build honours <c>HOME</c> and reads no other global config. That is a property of the build, so
/// the recorded minimum is the oldest build this daemon runs; <c>agy</c> auto-updates mid-session, and
/// an upgrade needs no action. The floor is state in <see cref="Core.ReviewerVersionStore"/>, not an
/// environment variable a shell profile could re-affirm.</para>
/// <para><c>AntigravityHostedAgentRuntimeFactory.LaunchRefusal</c> is the only caller, for both
/// advertisement and launch. The denial texts reach hosted launches too, so they describe the
/// containment, not the reviewer.</para>
/// </summary>
internal static class AntigravityReviewerCapability {
    static readonly ConsentAndMinimumGate<AntigravityReviewerDecision> Gate = new(
        allowed:             AntigravityReviewerDecision.Allowed,
        disabled:            AntigravityReviewerDecision.Disabled,
        versionUnresolved:   AntigravityReviewerDecision.VersionUnresolved,
        versionNoMinimum:    AntigravityReviewerDecision.VersionNoMinimum,
        versionBelowMinimum: AntigravityReviewerDecision.VersionBelowMinimum,
        versionIncomparable: AntigravityReviewerDecision.VersionIncomparable);

    /// <summary>The decision. It has no platform arm: the per-launch home is owner-only on every host,
    /// and one that cannot be restricted refuses its own launch.</summary>
    internal static AntigravityReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) =>
        Gate.Decide(operatorEnabled, installedVersion, minimumVersion);

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
