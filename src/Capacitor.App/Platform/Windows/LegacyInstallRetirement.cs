using System.Globalization;
using static Capacitor.App.Platform.Windows.LegacyRetirementStep;

namespace Capacitor.App.Platform.Windows;

/// <summary>
/// Runs a <see cref="LegacyRetirementPlan"/>, logging every step. A failed step does not stop the others, except
/// that the legacy root stays while a process the plan meant to stop still runs, or a daemon still runs from it:
/// deleting it then would strand that daemon. A root that cannot be moved aside loses its updater instead, so it no
/// longer updates itself back over this install's uninstall entry, and the next launch tries the root again.
/// </summary>
public static class LegacyInstallRetirement {
    public static async Task<LegacyRetirementOutcome> RunAsync(
            IReadOnlyList<LegacyRetirementStep> steps, ILegacyRetirementEffects effects,
            Action<string, (string Key, string? Value)[]> log, CancellationToken ct) {
        string? keptBecause = steps.Any(step => step is RemoveRoot) ? null : "its folder holds kcap's settings or daemon data";
        var failures    = new List<string>();
        var updaterGone = false;

        bool Attempt(string evt, string? target, string failure, Action action) {
            try {
                action();
                log(evt, [("target", target), ("result", "ok")]);
                return true;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                log(evt, [("target", target), ("result", "failed"), ("error", ex.Message)]);
                failures.Add(failure);
                return false;
            }
        }

        foreach (var step in steps) {
            ct.ThrowIfCancellationRequested();
            switch (step) {
                case StopProcesses stop:
                    foreach (var process in stop.Processes) {
                        var pid     = process.Pid.ToString(CultureInfo.InvariantCulture);
                        var running = $"{Path.GetFileName(process.Path)} is still running";
                        var stopped = Attempt("legacy_process_stopped", $"{pid} {process.Path}", running, () => {
                            if (!effects.EndProcess(process)) throw new IOException("it did not exit");
                        });
                        if (!stopped) keptBecause ??= running;
                    }
                    break;

                case RepointDaemons repoint:
                    string? pending;
                    try {
                        pending = await effects.RepointDaemonsAsync(repoint.From, ct).ConfigureAwait(false);
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        pending = ex.Message;
                    }
                    log("legacy_daemon_repointed", [("from", repoint.From), ("result", pending is null ? "ok" : "failed"), ("error", pending)]);
                    if (pending is not null) keptBecause ??= "the daemon still runs from its folder";
                    break;

                case RemoveShortcuts shortcuts:
                    foreach (var path in shortcuts.Paths)
                        Attempt("legacy_shortcut_removed", path, $"the shortcut {path}", () => effects.DeleteShortcut(path));
                    break;

                case RemoveAutostart autostart:
                    foreach (var name in autostart.ValueNames)
                        Attempt("legacy_autostart_removed", name, $"the sign-in entry {name}", () => effects.DeleteAutostart(name));
                    break;

                case RepointUninstallEntry entry:
                    Attempt("legacy_uninstall_entry_repointed", entry.To, "the Installed apps entry",
                        () => effects.RepointUninstallEntry(entry.From, entry.To));
                    break;

                case MoveUserPath move:
                    Attempt("legacy_path_moved", move.FromCli, "its folder on your PATH", () => effects.MoveUserPath(move.FromCli, move.ToCli));
                    break;

                case RemoveRoot remove:
                    if (keptBecause is not null) {
                        log("legacy_root_kept", [("target", remove.Root), ("reason", keptBecause)]);
                        break;
                    }
                    try {
                        effects.RemoveRoot(remove.Root);
                        log("legacy_root_removed", [("target", remove.Root), ("result", "ok")]);
                        updaterGone = true;
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        keptBecause = $"a file in {remove.Root} is in use";
                        log("legacy_root_kept", [("target", remove.Root), ("reason", keptBecause), ("error", ex.Message)]);
                        updaterGone = Attempt("legacy_updater_removed", remove.Root, "its updater", () => effects.RemoveUpdater(remove.Root));
                    }
                    break;

                case RemoveUninstallEntries entries:
                    if (!updaterGone) break;
                    foreach (var key in entries.Keys)
                        Attempt("legacy_uninstall_entry_removed", key, $"the Installed apps entry {key}", () => effects.DeleteUninstallEntry(key, entries.Root));
                    break;
            }
        }

        var outcome = new LegacyRetirementOutcome(keptBecause, failures);
        log("legacy_retired", [("kept_because", keptBecause), ("failures", failures.Count.ToString(CultureInfo.InvariantCulture))]);
        return outcome;
    }
}
