using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>One takeover batch, in order: preflight, intent, integrations, PATH, the daemon services, then the deletion
/// check and the deletion. Every launch starts from fresh evidence; an install the record holds pending stays pending
/// however little of it the evidence still sees, since a finished switch leaves nothing that names it. Each run says
/// whether a recheck should follow; a launch that changed nothing because it was held asks for none, since its record
/// still carries the holders an earlier launch found.</summary>
internal sealed class AdoptionBatch(
        IAdoptionMachine machine, AdoptionRecordStore store, AsideRetirement retirement, AdoptionIntegrations integrations,
        AdoptionPathStep path, AdoptionNotices notices, HarnessRegistry harnesses, AdoptionBundle bundle,
        IReadOnlyList<string> stableFolders, Func<string, bool> userCanWrite, Action<string, (string Key, string? Value)[]> log)
    : IDisposable {
    internal const string EvidenceUnavailable = "evidence_unavailable";
    internal const string MachineSwitched     = "machine_switched";

    readonly AdoptionServiceMove _services = new(machine, bundle, log);
    readonly AdoptionDeletion    _deletion = new(machine, store, retirement, harnesses, bundle.Root, log);
    volatile bool _awaitingDaemon;
    volatile bool _foundInstall;
    volatile bool _awaitingIdle;

    /// <summary>Whether a run this launch found an install to take over, held or not: the ordinary offer to put kcap on
    /// PATH waits for a launch that finds none, without that wait using up the one offer.</summary>
    public bool FoundInstall => _foundInstall;

    /// <summary>Whether the last launch run left a daemon move waiting for a safe point: running it again from fresh
    /// evidence is what moves it.</summary>
    public bool AwaitingDaemon => _awaitingDaemon;

    /// <summary>Whether one of those moves waits because the app saw its daemon busy or had no report from it, which
    /// the daemon turning idle settles. A refusal the daemon or the install gave waits for a recheck instead.</summary>
    public bool AwaitingIdle => _awaitingIdle;

    public async Task<bool> RunOnceAsync(CancellationToken ct) {
        _awaitingDaemon = false;
        _awaitingIdle   = false;
        var unfinished = FinishInterrupted();

        AdoptionEvidence evidence;
        try {
            evidence = await machine.ReadEvidenceAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            log("adoption_held", [("reason", EvidenceUnavailable), ("result", ex.Message)]);
            return false;
        }
        foreach (var found in evidence.Installs) log("adoption_found", AdoptionEvent.For(found));

        var record = store.Load();
        var plan = AdoptionPreflight.Plan(evidence, record, bundle.Version, bundle.Cli, bundle.InSystemApplications, userCanWrite, stableFolders);
        if (plan.Installs.Count > 0) _foundInstall = true;
        if (plan.HoldReason is { } hold) {
            log("adoption_held", [("reason", hold)]);
            return false;
        }
        if (record.MachineSwitched) {
            Report(plan.Reported, record);
            return Finish([]);
        }

        if (plan.Installs.Count > 0 || record.Pending) record = RecordIntent(plan.Installs);

        var pending = record.Installs.Where(i => i.State == InstallState.Pending).Select(i => i.Install).ToList();
        if (pending.Count == 0) return Finish([]);

        if (!await IntegrateAsync(pending, record.CutoverUtc, ct).ConfigureAwait(false)) return Finish([]);

        var linked = await path.ApplyAsync(plan, evidence, bundle.Cli, ct).ConfigureAwait(false);
        log("adoption_path", [("result", linked ? "ok" : "failed")]);
        if (!linked) return Finish([]);

        var (moved, deferred) = await _services.MoveAsync(pending, evidence, ct).ConfigureAwait(false);
        _awaitingDaemon = deferred.Count > 0;
        _awaitingIdle   = deferred.Any(d => d.Reason == AdoptionRepoint.Busy);
        if (moved.Count > 0) {
            var processes = machine.ReadProcesses();
            var sites     = await machine.ReadSitesAsync(ct).ConfigureAwait(false);
            await _deletion.SettleAsync(moved, processes, sites, unfinished, ct).ConfigureAwait(false);
        }

        return Finish(moved);
    }

    /// <summary>The deletion check alone, for installs a launch run has already moved. One whose recorded holder is
    /// still the same process is left without reading anything; one no launch run has carried to the check is left to
    /// the next one, which alone proves its PATH and daemon moved.</summary>
    public async Task<bool> RecheckAsync(CancellationToken ct) {
        var record = store.Load();
        if (!record.Pending) return false;

        var due = record.Installs
            .Where(i => i.State == InstallState.Pending && i.HeldBy is not null && !StillHeld(i))
            .Select(i => i.Install).ToList();
        if (due.Count == 0) return true;

        var unfinished = FinishInterrupted();
        var processes  = machine.ReadProcesses();
        var sites      = await machine.ReadSitesAsync(ct).ConfigureAwait(false);
        if (record.CutoverUtc is { } cutover && Named(due, cutover, sites)) {
            var done = await integrations.RepointAsync(foundNamed: true, ct).ConfigureAwait(false);
            log("adoption_integrations", [("result", done ? "ok" : "failed"), ("reason", "named")]);
        }
        await _deletion.SettleAsync(due, processes, sites, unfinished, ct).ConfigureAwait(false);

        return Finish([]);
    }

    public void Dispose() => notices.Dispose();

    // Asides a crash left behind are finished before anything else is retired. Null when the retirement record could
    // not be read, which holds every deletion this run.
    IReadOnlyList<string>? FinishInterrupted() {
        try {
            var unfinished = retirement.FinishInterrupted();
            foreach (var entry in unfinished) log("adoption_retired", [("install", entry), ("result", "failed"), ("reason", "aside_not_deleted")]);
            return unfinished;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            log("adoption_retired", [("result", "failed"), ("reason", ex.Message)]);
            return null;
        }
    }

    bool StillHeld(RecordedInstall install) =>
        install is { HolderPid: { } pid, HolderStartToken: { } token } && machine.StillRuns(pid, token);

    // Holders are cleared for the deletion check to record afresh: a recheck acts only on installs this launch carried
    // that far.
    AdoptionRecord RecordIntent(IReadOnlyList<InstallPlan> plans) {
        var left = new List<InstallPlan>();
        var record = store.Update(r => {
            left.Clear();
            var installs = r.Installs
                .Select(i => i.State == InstallState.Pending ? i with { HeldBy = null, HolderPid = null, HolderStartToken = null } : i)
                .ToList();
            foreach (var plan in plans) {
                var at = installs.FindIndex(i => AdoptionPreflight.SameRoot(i.Install, plan.Install));
                if (plan.Left is { } reason) {
                    if (at >= 0 && installs[at] is { State: InstallState.Left } kept && kept.Install.Identity == plan.Install.Identity) continue;
                    left.Add(plan);
                }
                var next = plan.Left is null
                    ? new RecordedInstall(plan.Install, InstallState.Pending, null, null, null, null)
                    : new RecordedInstall(plan.Install, InstallState.Left, plan.Left, plan.Cause, null, null);
                if (at >= 0) installs[at] = next;
                else installs.Add(next);
            }
            return r with { Installs = installs };
        });

        foreach (var plan in left) log("adoption_left", AdoptionEvent.For(plan.Install, reason: plan.Left.ToString()));
        return record;
    }

    async Task<bool> IntegrateAsync(IReadOnlyList<OtherCliInstall> pending, DateTime? cutover, CancellationToken ct) {
        var named = cutover is { } at && Named(pending, at, await machine.ReadSitesAsync(ct).ConfigureAwait(false));
        var done  = await integrations.RepointAsync(named, ct).ConfigureAwait(false);
        log("adoption_integrations", [("result", done ? "ok" : "failed"), ("reason", named ? "named" : null)]);
        return done;
    }

    // Whether an integration names an install again. Its services name it until the daemon moves, which the refresh
    // does not do, so they never rerun the refresh or move the cutover.
    bool Named(IReadOnlyList<OtherCliInstall> installs, DateTime cutover, IReadOnlyList<ReferenceSite> sites) {
        var integrationSites = sites.Where(s => !AdoptionReferenceSites.IsService(s)).ToList();
        return installs.Any(i => AdoptionConsumers.Holder(i, cutover, [], integrationSites, harnesses, bundle.Root) is not null);
    }

    void Report(IReadOnlyList<OtherCliInstall> reported, AdoptionRecord record) {
        foreach (var install in reported) {
            if (record.Installs.Any(r => r.State != InstallState.Pending && AdoptionPreflight.SameRoot(r.Install, install)
                    && r.Install.Identity == install.Identity)) continue;

            log("adoption_kept", AdoptionEvent.For(install, reason: MachineSwitched));
            notices.Post(AdoptionNotices.ReportedKey(install), AdoptionNotices.Reported(install));
        }
    }

    // The machine has had its switch once nothing is pending; every outcome not yet shown is posted again, so a notice
    // lost to a quit before the window appeared is told on a later launch.
    bool Finish(IReadOnlyList<OtherCliInstall> moved) {
        var record = store.Load();
        if (!record.MachineSwitched && Settled(record)) {
            record = store.Update(r => !r.MachineSwitched && Settled(r) ? r with { MachineSwitched = true } : r);
            if (record.MachineSwitched) log("adoption_switched", [("result", "machine")]);
        }

        foreach (var recorded in record.Installs) {
            var install = recorded.Install;
            switch (recorded.State) {
                case InstallState.Switched:
                    notices.Post(AdoptionNotices.SwitchedKey(install), AdoptionNotices.Switched(install, removed: true));
                    break;
                case InstallState.Pending when moved.Any(m => AdoptionPreflight.SameRoot(m, install)):
                    notices.Post(AdoptionNotices.SwitchedKey(install), AdoptionNotices.Switched(install, removed: false));
                    break;
                case InstallState.Left when recorded.Reason is { } reason:
                    notices.Post(AdoptionNotices.LeftKey(install, reason), AdoptionNotices.Left(install, reason, recorded.HeldBy));
                    break;
            }
        }

        return record.Pending;
    }

    // An install left for a cause the user can remove is still to be switched once it is gone, which a machine
    // recorded as switched would only report.
    static bool Settled(AdoptionRecord record) =>
        record.Installs.Count > 0 && record.Installs.All(i =>
            i.State == InstallState.Switched || i is { State: InstallState.Left, Reason: { } reason } && !AdoptionPreflight.Rederived(reason));
}
