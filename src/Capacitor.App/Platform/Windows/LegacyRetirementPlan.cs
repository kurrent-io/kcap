namespace Capacitor.App.Platform.Windows;

/// <summary>
/// The steps that leave this install the only kcap on the machine, in order. The updater stops before the app:
/// stopped after it, it would apply a pending update and start the app again. The daemon's task moves before the
/// legacy root goes, since its daemon runs from there. The root goes last, and never when it holds a folder kcap
/// keeps, such as the config directory or the daemon store.
/// </summary>
public static class LegacyRetirementPlan {
    static readonly string[] StoppedImages = [VelopackRoot.UpdateExe, VelopackRoot.AppExecutable, VelopackRoot.LegacyAppExecutable];

    public static IReadOnlyList<LegacyRetirementStep> Build(LegacyRetirementFacts facts, int ownPid) {
        var root  = facts.Legacy.Root;
        var steps = new List<LegacyRetirementStep>();

        var stopped = facts.Processes
            .Where(p => p.Pid != ownPid && LegacyPaths.Within(p.Path, root)
                && StoppedImages.Contains(Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => string.Equals(Path.GetFileName(p.Path), VelopackRoot.UpdateExe, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
        if (stopped.Length > 0) steps.Add(new LegacyRetirementStep.StopProcesses(stopped));

        steps.Add(new LegacyRetirementStep.RepointDaemons(root));

        if (facts.Shortcuts.Count > 0) steps.Add(new LegacyRetirementStep.RemoveShortcuts(facts.Shortcuts));
        if (facts.Autostart.Count > 0) steps.Add(new LegacyRetirementStep.RemoveAutostart(facts.Autostart));

        var named = facts.UninstallEntries.Where(e => LegacyPaths.Same(e.InstallLocation, root)).ToArray();
        if (named.Any(e => string.Equals(e.Key, UninstallEntry.KcapKey, StringComparison.OrdinalIgnoreCase)))
            steps.Add(new LegacyRetirementStep.RepointUninstallEntry(root, facts.OwnRoot));

        if (facts.LegacyCliOnPath is { } legacyCli) steps.Add(new LegacyRetirementStep.MoveUserPath(legacyCli, facts.OwnCli));

        if (facts.KeptPaths.Any(kept => LegacyPaths.Within(kept, root))) return steps;

        steps.Add(new LegacyRetirementStep.RemoveRoot(root));
        var others = named.Where(e => !string.Equals(e.Key, UninstallEntry.KcapKey, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToArray();
        if (others.Length > 0) steps.Add(new LegacyRetirementStep.RemoveUninstallEntries(root, others));
        return steps;
    }
}
