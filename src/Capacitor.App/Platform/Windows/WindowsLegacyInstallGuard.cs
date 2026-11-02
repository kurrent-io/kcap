using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Velopack.Locators;

namespace Capacitor.App.Platform.Windows;

/// <summary>
/// Leaves one desktop install on the machine, the one in the <c>kcap</c> folder. A copy launched from a legacy
/// Kurrent Capacitor root hands the launch to that install; that install retires a legacy root it finds. The daemon
/// task it repoints is the daemon lane's to change once the lane exists, so this runs before the lane is built.
/// Never fails a launch: anything unexpected is logged and the launch goes on.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsLegacyInstallGuard {
    /// <param name="keptPaths">Folders kcap keeps, which a legacy root holding one of them keeps too.</param>
    /// <returns>Whether this copy handed its launch over and must exit, and the retirement it ran, if any.</returns>
    public static async Task<(bool HandedOver, LegacyRetirementOutcome? Retirement)> RunAsync(
            string cliDirectory, ConfigRoot config, IReadOnlyList<string> keptPaths, IProcessRunner runner,
            Action<string, (string Key, string? Value)[]> log, CancellationToken ct) {
        try {
            if (!VelopackLocator.IsCurrentSet) return (false, null);
            var locator = VelopackLocator.Current;
            if (locator.IsPortable || locator.CurrentlyInstalledVersion is not { } installed || locator.RootAppDir is not { Length: > 0 } ownRoot)
                return (false, null);
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            if (LegacyWindowsInstall.Successor(ownRoot, localAppData) is { } successor) {
                log("legacy_handover", [("from", ownRoot), ("to", successor)]);
                Process.Start(new ProcessStartInfo(successor) { UseShellExecute = false })?.Dispose();
                return (true, null);
            }

            var parents = new[] { localAppData, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ownRoot)) }.OfType<string>();
            await Task.Run(() => WindowsLegacyRetirementEffects.SweepRetired(parents), ct).ConfigureAwait(false);

            var entries = WindowsLegacyRetirementEffects.UninstallEntries();
            if (LegacyWindowsInstall.Find(ownRoot, installed.ToString(), localAppData, entries.Select(e => e.InstallLocation)) is not { } legacy)
                return (false, null);
            log("legacy_install_found", [("root", legacy.Root), ("version", legacy.Version), ("own_root", ownRoot)]);

            var ownCli = Path.Combine(cliDirectory, CliResolver.BundledCliFileName);
            var facts  = await Task.Run(() => WindowsLegacyRetirementEffects.Scan(legacy, ownRoot, ownCli, entries, keptPaths), ct)
                .ConfigureAwait(false);
            var steps  = LegacyRetirementPlan.Build(facts, Environment.ProcessId);
            log("legacy_retirement_planned", [("steps", string.Join(", ", steps.Select(step => step.GetType().Name)))]);

            var effects = new WindowsLegacyRetirementEffects(runner, ownCli, config);
            return (false, await Task.Run(() => LegacyInstallRetirement.RunAsync(steps, effects, log, ct), ct).ConfigureAwait(false));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return (false, null);
        } catch (Exception ex) {
            log("legacy_retirement_failed", [("error", ex.Message)]);
            return (false, null);
        }
    }
}
