namespace Capacitor.App.Platform.Windows;

/// <param name="KeptBecause">Why the legacy root is still there, or null once it is gone.</param>
/// <param name="Failures">The steps that failed, as the user reads them.</param>
public sealed record LegacyRetirementOutcome(string? KeptBecause, IReadOnlyList<string> Failures) {
    public string Message => KeptBecause is { } reason
        ? $"kcap could not remove Kurrent Capacitor yet: {reason}. It tries again the next time it opens."
        : Failures.Count == 0
            ? "kcap replaced Kurrent Capacitor. The daemon now runs from this install, and settings and sign-in are kept."
            : $"kcap replaced Kurrent Capacitor, and settings and sign-in are kept. Some of its leftovers remain: {string.Join("; ", Failures)}.";
}
