using System.Xml;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Moves each install's daemon services onto the bundle's daemon and proves every one of them moved. The lane
/// proves only the app's own daemon, and only while it runs: the repoint also moves the install's other services and
/// its stopped ones, and each of those counts when its plist names the bundle's daemon and launchd holds no job under
/// its label or one loaded from that daemon. A service that ran before must be running again.</summary>
internal sealed class AdoptionServiceMove(IAdoptionMachine machine, AdoptionBundle bundle, Action<string, (string Key, string? Value)[]> log) {
    const string DaemonBusy = "daemon_busy";

    /// <summary>A refusal a later attempt can get past: the daemon was not at a safe point, or the install changed.</summary>
    internal static bool Deferred(MutationOutcome outcome) =>
        outcome is MutationOutcome.Refused { Reason: AdoptionRepoint.Busy or AdoptionRepoint.InstallChanged or DaemonBusy };

    /// <summary>The installs whose services all moved, an install with none included. The daemon client is asked to
    /// attach again once, after the last repoint.</summary>
    public async Task<IReadOnlyList<OtherCliInstall>> MoveAsync(
            IReadOnlyList<OtherCliInstall> installs, AdoptionEvidence evidence, CancellationToken ct) {
        var moved = new List<OtherCliInstall>();
        var asked = false;
        foreach (var install in installs) {
            var units = evidence.Units.Where(u => Inside(install, u.Program) || Inside(install, u.LoadedProgram)).ToList();
            if (units.Count == 0) {
                moved.Add(install);
                continue;
            }

            asked = true;
            var outcome = await machine.RepointAsync(install, c => StillSameAsync(install, units, c), ct).ConfigureAwait(false);
            if (Deferred(outcome)) {
                var reason = ((MutationOutcome.Refused)outcome).Reason;
                log("adoption_repoint", AdoptionEvent.For(install, "deferred", reason));
                log("adoption_held", AdoptionEvent.For(install, reason: reason));
                continue;
            }
            if (await FailureAsync(outcome, units, ct).ConfigureAwait(false) is { } failure) {
                log("adoption_repoint", AdoptionEvent.For(install, "failed", failure));
                continue;
            }
            log("adoption_repoint", AdoptionEvent.For(install, "ok"));
            moved.Add(install);
        }

        if (asked) await machine.ReattachAsync().ConfigureAwait(false);
        return moved;
    }

    // Right before the stop: an npm update during the wait for a safe point puts a different daemon in place.
    async Task<bool> StillSameAsync(OtherCliInstall install, List<ServiceUnitFact> units, CancellationToken ct) {
        if (await machine.ReadInstallAsync(install.CliBinary, ct).ConfigureAwait(false) is not { } now
         || now.Identity != install.Identity || now.Version != install.Version) return false;

        try {
            foreach (var unit in units) {
                var job = await machine.ReadJobAsync(unit.Name, ct).ConfigureAwait(false);
                if (job.Loaded != unit.Loaded || job.Pid != unit.JobPid) return false;
            }
        } catch (IOException) {
            return false;
        }

        return true;
    }

    async Task<string?> FailureAsync(MutationOutcome outcome, List<ServiceUnitFact> units, CancellationToken ct) {
        switch (outcome) {
            case MutationOutcome.Refused refused: return refused.Reason;
            case MutationOutcome.Failed failed:   return failed.Reason ?? $"exit_{failed.ExitCode}";
        }

        var own = machine.AppDaemonName;
        if (own is not null && units.Exists(u => Running(u) && DaemonStore.Sanitize(u.Name) == DaemonStore.Sanitize(own))
         && outcome is not MutationOutcome.Succeeded) return Describe(outcome);

        foreach (var unit in units) {
            if (!IsBundleDaemon(PlistProgram(unit.PlistPath))) return $"unit_not_moved:{unit.Name}";

            LaunchdJob job;
            try {
                job = await machine.ReadJobAsync(unit.Name, ct).ConfigureAwait(false);
            } catch (IOException) {
                return $"unit_unknown:{unit.Name}";
            }
            if (job.Loaded && !IsBundleDaemon(job.Program)) return $"unit_not_moved:{unit.Name}";
            if (Running(unit) && job.Pid is null) return $"unit_stopped:{unit.Name}";
        }

        return null;
    }

    static bool Running(ServiceUnitFact unit) => unit is { Loaded: true, JobPid: not null };

    static bool Inside(OtherCliInstall install, string? path) =>
        path is not null && install.OwnedEntries.Prepend(install.Root).Any(folder => CanonicalPath.IsWithin(path, folder));

    bool IsBundleDaemon(string? path) =>
        path is not null && PathComparison.Equal(CanonicalPath.Resolve(path), CanonicalPath.Resolve(bundle.Daemon));

    static string? PlistProgram(string plist) {
        try {
            return LaunchdPrint.Binary(File.ReadAllText(plist));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) {
            return null;
        }
    }

    static string Describe(MutationOutcome outcome) => outcome switch {
        MutationOutcome.AttentionSkew skew     => "skew:" + skew.Detail,
        MutationOutcome.AttentionRepair repair => "repair:" + repair.Detail,
        MutationOutcome.Unconfirmed unknown    => "unconfirmed:" + unknown.Detail,
        MutationOutcome.UnconfirmedNoAttach    => "unconfirmed",
        _                                      => outcome.GetType().Name,
    };
}
