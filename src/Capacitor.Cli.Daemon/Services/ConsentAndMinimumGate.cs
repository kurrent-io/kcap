using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A reviewer gate that is the operator's consent, then the recorded minimum build, and nothing else,
/// answered in one vendor's own decision type. Every verdict is a constructor argument, so a comparison
/// outcome added to the shared type is a build error here and at each vendor, never a silent admit.
/// </summary>
internal sealed class ConsentAndMinimumGate<TDecision>(
        TDecision allowed,
        TDecision disabled,
        TDecision versionUnresolved,
        TDecision versionNoMinimum,
        TDecision versionBelowMinimum,
        TDecision versionIncomparable) {
    /// <param name="installedVersion">The version of the binary the launch will run; null when it could
    /// not be resolved, which is unknown and therefore refused.</param>
    internal TDecision Decide(bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Consent first and short-circuiting: a wedged vendor binary must not be probed, let alone
        // stall a daemon start, for a feature the operator switched off.
        if (!operatorEnabled) return disabled;

        // No discard arm: a new comparison outcome must fail the build rather than be admitted.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => allowed,
            ReviewerVersionAffirmation.Unresolved        => versionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => versionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => versionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => versionIncomparable
        };
    }
}
