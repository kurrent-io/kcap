using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Config;

/// <summary>The one rule that turns the saved switch and the daemon's report into a state; the CLI and the
/// desktop app both render from it.</summary>
public static class ExperimentalFeatureState {
    /// <param name="saved">The switch as stored, false when the profile never set it: what the daemon reports
    /// as <see cref="ExperimentalFeatureStatusDto.ResolvedSaved"/>.</param>
    /// <param name="on">What the switch takes effect as, the feature's default standing in for an unset one.</param>
    public static (ExperimentalFeatureStateKind Kind, string? Reason) Classify(
        bool saved, bool on, bool daemonRunning, DaemonStatusDto? status, string featureId) {
        if (!daemonRunning) return (ExperimentalFeatureStateKind.DaemonNotRunning, null);

        var feature = status?.Experimental?.FirstOrDefault(f => string.Equals(f.Id, featureId, StringComparison.Ordinal));
        if (feature is null) return (ExperimentalFeatureStateKind.Unknown, null);

        // A daemon that reports no effective value cannot say what a default does for an unset switch.
        if (feature.ResolvedOn is null && saved != on) return (ExperimentalFeatureStateKind.Unknown, null);
        var resolvedOn = feature.ResolvedOn ?? feature.ResolvedSaved;

        if (feature.ResolvedSaved != saved || resolvedOn != on) return (ExperimentalFeatureStateKind.Applying, null);
        if (on && !feature.Active) return (ExperimentalFeatureStateKind.OnNotActive, feature.Reason);

        // A commit is reported before the re-registration that carries it, so it is still applying until
        // that registration fails.
        if (!feature.ServerAcknowledged) {
            return feature.RegistrationFailed == true
                ? (ExperimentalFeatureStateKind.AppliedLocally, null)
                : (ExperimentalFeatureStateKind.Applying, null);
        }

        return feature.Active
            ? (ExperimentalFeatureStateKind.Active, null)
            : (ExperimentalFeatureStateKind.Off, null);
    }
}
