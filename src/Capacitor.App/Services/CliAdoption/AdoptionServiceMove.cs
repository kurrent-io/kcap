using System.Xml;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Moves each install's daemon services onto the bundle's daemon and proves every one of them moved. The lane
/// proves only the app's own daemon, and only while it runs: the repoint also moves the install's other services and
/// its stopped ones, and each of those counts when its plist names the bundle's daemon and launchd holds no job under
/// its label or one loaded from that daemon. A service that ran before must be running again. Once the repoint has
/// rewritten a unit nothing names the install any more, so a move its CLI ran without the lane proving it is recorded
/// unproven, and the install is kept as the fallback until a later run proves those units moved.</summary>
internal sealed class AdoptionServiceMove(
        IAdoptionMachine machine, AdoptionRecordStore store, AdoptionBundle bundle, Action<string, (string Key, string? Value)[]> log) {
    const string DaemonBusy   = "daemon_busy";
    const string MoveUnproven = "move_unproven";
    const string FallbackGone = "fallback_gone";

    /// <summary>A refusal a later attempt can get past: the daemon was not at a safe point, or the install changed.</summary>
    internal static bool Deferred(MutationOutcome outcome) =>
        outcome is MutationOutcome.Refused { Reason: AdoptionRepoint.Busy or AdoptionRepoint.InstallChanged or DaemonBusy };

    /// <summary>The installs whose services all moved, an install with none included, and the refusal each deferred
    /// one met. The daemon client is asked to attach again once, after the last repoint that reached the lane's CLI:
    /// a new attach reports the daemon idle, which must not be read as the safe point a deferral waits for.</summary>
    public async Task<(IReadOnlyList<OtherCliInstall> Moved, IReadOnlyList<(OtherCliInstall Install, string Reason)> Deferred)> MoveAsync(
            IReadOnlyList<OtherCliInstall> installs, AdoptionEvidence evidence, CancellationToken ct) {
        var moved    = new List<OtherCliInstall>();
        var deferred = new List<(OtherCliInstall, string)>();
        var ran      = false;
        foreach (var install in installs) {
            var units    = evidence.Units.Where(u => Inside(install, u.Program) || Inside(install, u.LoadedProgram)).ToList();
            var unproven = Unproven(store.Load(), install);
            if (units.Count == 0 && unproven is null) {
                moved.Add(install);
                continue;
            }
            // A fallback removed by hand is nothing to keep, and a repoint would refuse an install it cannot read.
            if (units.Count == 0 && !install.OwnedEntries.Prepend(install.Root).Any(Exists)) {
                store.Update(r => r with { UnprovenMoves = [.. r.UnprovenMoves.Where(m => !Names(m, install))] });
                log("adoption_repoint", AdoptionEvent.For(install, "ok", FallbackGone));
                moved.Add(install);
                continue;
            }
            var proving = units.Concat(unproven?.Units.Where(r => !units.Exists(u => u.Name == r.Name)) ?? []).ToList();

            var outcome = await machine.RepointAsync(install, c => StillSameAsync(install, units, c), ct).ConfigureAwait(false);
            ran |= outcome is not MutationOutcome.Refused;
            if (Deferred(outcome)) {
                var reason = ((MutationOutcome.Refused)outcome).Reason;
                log("adoption_repoint", AdoptionEvent.For(install, "deferred", reason));
                log("adoption_held", AdoptionEvent.For(install, reason: reason));
                deferred.Add((install, reason));
                continue;
            }
            if (await FailureAsync(outcome, proving, ct).ConfigureAwait(false) is { } failure) {
                log("adoption_repoint", AdoptionEvent.For(install, "failed", failure.Reason));
                // Not rolled back to the old plist: the CLI already rolls back a bootstrap that fails. A unit whose plist
                // already names the bundle is out of the next evidence's sight however the lane did.
                if (outcome is not (MutationOutcome.Refused or MutationOutcome.Succeeded) || failure.Rewritten)
                    RecordUnproven(install, failure.Daemon, proving);
                continue;
            }
            log("adoption_repoint", AdoptionEvent.For(install, "ok"));
            if (unproven is not null) store.Update(r => r with { UnprovenMoves = [.. r.UnprovenMoves.Where(m => !Names(m, install))] });
            moved.Add(install);
        }

        if (ran) await machine.ReattachAsync().ConfigureAwait(false);
        return (moved, deferred);
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

    /// <summary>The move a repoint left unproven for <paramref name="install"/>, if any.</summary>
    internal static UnprovenMove? Unproven(AdoptionRecord record, OtherCliInstall install) =>
        record.UnprovenMoves.FirstOrDefault(m => Names(m, install));

    static bool Names(UnprovenMove move, OtherCliInstall install) =>
        PathComparison.Equal(CanonicalPath.Resolve(move.InstallRoot), CanonicalPath.Resolve(install.Root));

    void RecordUnproven(OtherCliInstall install, string? daemon, List<ServiceUnitFact> units) {
        store.Update(r => r with {
            UnprovenMoves = [.. r.UnprovenMoves.Where(m => !Names(m, install)), new UnprovenMove(install.Root, daemon, units)],
        });
        log("adoption_kept", AdoptionEvent.For(install, reason: $"{MoveUnproven}:{daemon}"));
    }

    // The first reason the move is not proven, with the daemon it concerns and whether that unit's plist already names the
    // bundle. With no unit left to check, as from a record that lost them, only the lane's proof of the app's own daemon
    // counts.
    async Task<(string Reason, string? Daemon, bool Rewritten)?> FailureAsync(MutationOutcome outcome, List<ServiceUnitFact> units, CancellationToken ct) {
        var own     = machine.AppDaemonName;
        var ownUnit = own is null ? null : units.Find(u => Running(u) && DaemonStore.Sanitize(u.Name) == DaemonStore.Sanitize(own));
        var blamed  = ownUnit?.Name ?? units.FirstOrDefault()?.Name ?? own;
        switch (outcome) {
            case MutationOutcome.Refused refused: return (refused.Reason, blamed, false);
            case MutationOutcome.Failed failed:   return (failed.Reason ?? $"exit_{failed.ExitCode}", blamed, false);
        }

        if ((ownUnit is not null || units.Count == 0) && outcome is not MutationOutcome.Succeeded) return (Describe(outcome), blamed, false);

        foreach (var unit in units) {
            if (!IsBundleDaemon(PlistProgram(unit.PlistPath))) return ($"unit_not_moved:{unit.Name}", unit.Name, false);

            LaunchdJob job;
            try {
                job = await machine.ReadJobAsync(unit.Name, ct).ConfigureAwait(false);
            } catch (IOException) {
                return ($"unit_unknown:{unit.Name}", unit.Name, true);
            }
            if (job.Loaded && !IsBundleDaemon(job.Program)) return ($"unit_not_moved:{unit.Name}", unit.Name, false);
            if (Running(unit) && job.Pid is null) return ($"unit_stopped:{unit.Name}", unit.Name, true);
        }

        return null;
    }

    static bool Running(ServiceUnitFact unit) => unit is { Loaded: true, JobPid: not null };

    static bool Exists(string path) => new FileInfo(path).LinkTarget is not null || Path.Exists(path);

    internal static bool Inside(OtherCliInstall install, string? path) =>
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
