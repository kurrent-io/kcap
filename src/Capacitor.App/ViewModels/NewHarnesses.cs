namespace Capacitor.App.ViewModels;

/// Coding tools this machine found after kcap was set up, by label, and how to record that they were offered once
/// the banner has led to Settings → Harnesses.
public sealed record NewHarnesses(IReadOnlyList<string> Labels, Action Offered);
