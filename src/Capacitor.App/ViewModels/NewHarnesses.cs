namespace Capacitor.App.ViewModels;

/// Coding tools this machine found after kcap was set up, by label, and how to record that they were offered once
/// the banner has led to Settings → Harnesses. Recording waits on a lock and the disk, so it runs off the caller's thread.
public sealed record NewHarnesses(IReadOnlyList<string> Labels, Func<Task> Offered);
