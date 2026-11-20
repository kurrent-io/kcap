using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Gemini;

internal enum GeminiReviewerDecision {
    Allowed,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether this daemon may run Gemini as an unattended review-flow reviewer. Pure, and both conditions
/// fail closed.
/// <para>A reviewer runs with the daemon's own HOME, so prompt-injected repository content can execute
/// with the daemon user's authority; that risk lands on the operator. The switch is an opt-out
/// (<c>KCAP_GEMINI_UNATTENDED_REVIEWER=0</c>) because the caller chooses the vendor and the ungated
/// vendors carry the same authority.</para>
/// <para>The boundary is Gemini's MCP allowlist acting as an exclusive exact-match gate the repository
/// cannot widen, which is a property of the build. So the operator affirms a minimum build
/// (<c>kcap daemon reviewer affirm --vendor gemini</c>), and an unrecorded minimum or an older or
/// unidentifiable build fails closed. A minimum keeps consent across upgrades and assumes the
/// allowlist only improves; if a release weakens it, raise the floor past it.</para>
/// <para>Stricter than interactive hosting on purpose: a broken MCP gate means repository-controlled
/// process execution.</para>
/// </summary>
internal static class GeminiReviewerCapability {
    static readonly ConsentAndMinimumGate<GeminiReviewerDecision> Gate = new(
        allowed:             GeminiReviewerDecision.Allowed,
        disabled:            GeminiReviewerDecision.Disabled,
        versionUnresolved:   GeminiReviewerDecision.VersionUnresolved,
        versionNoMinimum:    GeminiReviewerDecision.VersionNoMinimum,
        versionBelowMinimum: GeminiReviewerDecision.VersionBelowMinimum,
        versionIncomparable: GeminiReviewerDecision.VersionIncomparable);

    /// <summary>
    /// Pure decision. <paramref name="installedVersion"/> is the version of the binary this launch will
    /// actually use — null when it could not be resolved, which is treated as unknown and therefore denied.
    /// </summary>
    internal static GeminiReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) =>
        Gate.Decide(operatorEnabled, installedVersion, minimumVersion);

    /// <summary>
    /// The refusal reason, for a coded error an operator can act on. Separated from
    /// <see cref="Decide"/> so the two cannot disagree about WHY a launch was denied.
    /// </summary>
    internal static string DenialReason(
            GeminiReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            GeminiReviewerDecision.Disabled =>
                "gemini_unattended_reviewer_disabled: this daemon has EXPLICITLY disabled Gemini as an "
              + "unattended review-flow reviewer. Unset KCAP_GEMINI_UNATTENDED_REVIEWER in the daemon's "
              + "environment (not on the server) to restore the default, which is enabled — or set it "
              + "to 1. Worth knowing before you re-enable: a Gemini review grants prompt-injected "
              + "repository content code execution with this daemon user's authority. That is a real "
              + "risk, but it is the same posture as the never-gated Claude, Codex, Cursor and Copilot "
              + "reviewers, so this switch narrows nothing a requester cannot route around.",

            GeminiReviewerDecision.VersionUnresolved =>
                "gemini_reviewer_version_unresolved: the installed gemini version could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. The "
              + "reviewer's only containment is that build's MCP allowlist, so an unverifiable build is "
              + "refused.",

            GeminiReviewerDecision.VersionNoMinimum =>
                "gemini_reviewer_version_no_minimum: this daemon has no recorded minimum gemini "
              + "version, so there is nothing to check the installed build against. A daemon records "
              + "one automatically at startup, so the usual cause is that the version probe failed "
              + "then — check that `gemini --version` succeeds for the daemon user, and restart. To "
              + "record one now without restarting, run `kcap daemon reviewer affirm --vendor gemini`.",

            GeminiReviewerDecision.VersionIncomparable =>
                $"gemini_reviewer_version_incomparable: gemini {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor gemini`.",

            // Exhaustive, like Decide's switch and for a weaker but real version of the same reason: a
            // discard here would print the below-minimum text for a future arm that means something
            // else, telling an operator to fix the wrong thing. Allowed throws rather than returning a
            // string, because asking for the denial reason of a permitted decision is a caller bug.
            GeminiReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            GeminiReviewerDecision.VersionBelowMinimum =>
                $"gemini_reviewer_version_below_minimum: gemini {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. The "
              + "reviewer's containment rests on the build's MCP-allowlist semantics — an exclusive "
              + "exact-match gate the reviewed repository cannot widen — so an OLDER build than the "
              + "one recorded is refused. Upgrade gemini, or deliberately lower the minimum to the "
              + "installed build with `kcap daemon reviewer affirm --vendor gemini`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
