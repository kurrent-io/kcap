using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>One takeover batch, in order: preflight, intent, integrations, PATH, the daemon services, the deletion check
/// and the deletion, then each root-owned install behind its administrator prompt. Every launch starts from fresh
/// evidence; an install the record holds pending stays pending however little of it the evidence still sees, since a
/// finished switch leaves nothing that names it. Each run says whether a recheck should follow; a launch that changed
/// nothing because it was held asks for none, since its record still carries the holders an earlier launch found. Only
/// the launch run and <see cref="FinishAsync"/> reach the administrator prompt: a retry for a deferred daemon move and
/// a recheck never do, so the prompt never appears mid-session unasked.</summary>
internal sealed class AdoptionBatch(
        IAdoptionMachine machine, AdoptionRecordStore store, AsideRetirement retirement, AdoptionIntegrations integrations,
        AdoptionPathStep path, AdoptionNotices notices, IAppStateStore appState, HarnessRegistry harnesses, AdoptionBundle bundle,
        IReadOnlyList<string> stableFolders, Func<string, bool> userCanWrite, IPrivilegedSwap administrator, string helperFolder,
        Action<string, (string Key, string? Value)[]> log)
    : IDisposable {
    internal const string EvidenceUnavailable = "evidence_unavailable";
    internal const string MachineSwitched     = "machine_switched";
    internal const string RemovedByHand       = "removed_by_hand";

    readonly AdoptionServiceMove _services = new(machine, bundle, log);
    readonly AdoptionDeletion    _deletion = new(machine, store, retirement, harnesses, bundle.Root, log);
    readonly HashSet<string> _rootOwned = new(PathComparison.Comparer);
    AdoptionPrivilegedStep? _privileged;
    volatile bool _awaitingDaemon;
    volatile bool _awaitingIdle;

    /// <summary>Whether the last launch run left a daemon move waiting for a safe point: running it again from fresh
    /// evidence is what moves it.</summary>
    public bool AwaitingDaemon => _awaitingDaemon;

    /// <summary>Whether one of those moves waits because the app saw its daemon busy or had no report from it, which
    /// the daemon turning idle settles. A refusal the daemon or the install gave waits for a recheck instead.</summary>
    public bool AwaitingIdle => _awaitingIdle;

    /// <summary>Whether an install waits on <see cref="FinishAsync"/>: left behind a cancelled administrator prompt or
    /// a deletion cut short, or left for the app being installed for one user, once it runs from /Applications.</summary>
    public bool FinishOffered => store.Load().Installs.Any(i => i.State == InstallState.Left && i.Reason switch {
        LeftReason.PromptCancelled or LeftReason.DeletionCutShort => true,
        LeftReason.SharedInstallPrivateApp                        => bundle.InSystemApplications,
        _                                                         => false,
    });

    AdoptionPrivilegedStep Privileged =>
        _privileged ??= new(machine, store, retirement, _deletion, path, administrator, bundle, helperFolder, log);

    /// <summary>The launch's own run.</summary>
    public Task<bool> RunOnceAsync(CancellationToken ct) => RunAsync(prompt: true, finishing: false, ct);

    /// <summary>The batch again for a daemon move it deferred. A root-owned install it moves waits for the next launch
    /// or Finish switching.</summary>
    public Task<bool> RetryAsync(CancellationToken ct) => RunAsync(prompt: false, finishing: false, ct);

    /// <summary>The batch again, with every install left behind a cancelled prompt, a deletion cut short or an app
    /// installed for one user taken back in, and the prompt raised again where one is needed: nothing else retries
    /// them. A machine already switched stays switched.</summary>
    public Task<bool> FinishAsync(CancellationToken ct) => RunAsync(prompt: true, finishing: true, ct);

    async Task<bool> RunAsync(bool prompt, bool finishing, CancellationToken ct) {
        _awaitingDaemon = false;
        _awaitingIdle   = false;
        await Privileged.SettleAsync(ct).ConfigureAwait(false);
        var unfinished = FinishInterrupted();

        AdoptionEvidence evidence;
        try {
            evidence = await machine.ReadEvidenceAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            log("adoption_held", [("reason", EvidenceUnavailable), ("result", ex.Message)]);
            return false;
        }
        foreach (var found in evidence.Installs) log("adoption_found", AdoptionEvent.For(found));

        var record   = store.Load();
        var reopened = finishing && record.Installs.Any(Finishable);
        var plan = AdoptionPreflight.Plan(evidence, reopened ? Reopen(record) : record, bundle.Version, bundle.Cli,
            bundle.InSystemApplications, userCanWrite, stableFolders);
        if (plan.HoldReason is { } hold) {
            log("adoption_held", [("reason", hold)]);
            return false;
        }

        record = CompleteByHand(evidence, record);
        if (reopened) record = store.Update(Reopen);
        if (record.MachineSwitched) Report(plan.Reported, record);

        if (plan.Installs.Count > 0 || record.Pending) record = RecordIntent(plan.Installs);
        if (plan.Installs.Count > 0) await appState.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true }).ConfigureAwait(false);

        var pending = record.Installs.Where(i => i.State == InstallState.Pending).Select(i => i.Install).ToList();
        if (pending.Count == 0) return Finish([]);
        foreach (var install in pending.Where(i => NeedsAdministrator(i, plan))) _rootOwned.Add(Key(install));

        if (!await IntegrateAsync(pending, record.CutoverUtc, ct).ConfigureAwait(false)) return Finish([]);

        var linked = await path.ApplyAsync(plan, evidence, bundle.Cli, ct).ConfigureAwait(false);
        log("adoption_path", [("result", linked ? "ok" : "failed")]);
        if (!linked) return Finish([]);

        var (moved, deferred) = await _services.MoveAsync(pending, evidence, ct).ConfigureAwait(false);
        _awaitingDaemon = deferred.Count > 0;
        _awaitingIdle   = deferred.Any(d => d.Reason == AdoptionRepoint.Busy);
        var userOwned = moved.Where(i => !_rootOwned.Contains(Key(i))).ToList();
        if (userOwned.Count > 0) {
            var processes = machine.ReadProcesses();
            var sites     = await machine.ReadSitesAsync(ct).ConfigureAwait(false);
            await _deletion.SettleAsync(userOwned, processes, sites, unfinished, ct).ConfigureAwait(false);
        }

        if (prompt) {
            foreach (var install in moved.Where(i => _rootOwned.Contains(Key(i)))) {
                var planned = plan.Installs.FirstOrDefault(p => AdoptionPreflight.SameRoot(p.Install, install));
                await Privileged.RunAsync(install, planned, evidence, unfinished, ct).ConfigureAwait(false);
            }
        }

        return Finish(userOwned);
    }

    /// <summary>The deletion check alone, for installs a launch run has already moved. One whose recorded holder is
    /// still the same process is left without reading anything; one no launch run has carried to the check is left to
    /// the next one, which alone proves its PATH and daemon moved.</summary>
    public async Task<bool> RecheckAsync(CancellationToken ct) {
        var record = store.Load();
        if (!record.Pending) return false;

        var due = record.Installs
            .Where(i => i.State == InstallState.Pending && i.HeldBy is not null && !StillHeld(i) && !RootOwned(i.Install))
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

    static bool Finishable(RecordedInstall install) => install is {
        State: InstallState.Left, Reason: LeftReason.PromptCancelled or LeftReason.DeletionCutShort or LeftReason.SharedInstallPrivateApp,
    };

    static AdoptionRecord Reopen(AdoptionRecord record) => record with {
        Installs = [.. record.Installs.Select(i => Finishable(i)
            ? i with { State = InstallState.Pending, Reason = null, HeldBy = null, HolderPid = null, HolderStartToken = null }
            : i)],
    };

    static string Key(OtherCliInstall install) => CanonicalPath.Resolve(install.Root);

    // As preflight found it, or, for an install this launch's evidence no longer finds, as its own folders are now.
    bool NeedsAdministrator(OtherCliInstall install, AdoptionPlan plan) =>
        plan.Installs.FirstOrDefault(p => AdoptionPreflight.SameRoot(p.Install, install)) is { } planned
            ? planned.NeedsAdministrator
            : OwnFoldersNeedAdministrator(install);

    // A recheck never deletes what only the administrator prompt can, and never raises it.
    bool RootOwned(OtherCliInstall install) => _rootOwned.Contains(Key(install)) || OwnFoldersNeedAdministrator(install);

    bool OwnFoldersNeedAdministrator(OtherCliInstall install) =>
        install.OwnedEntries.Any(e => !userCanWrite(Path.GetDirectoryName(e)!));

    // A left install that is gone, while every PATH preflight reads finds the bundle's kcap, was finished by hand. Its
    // notice is recorded as shown, so none is posted.
    AdoptionRecord CompleteByHand(AdoptionEvidence evidence, AdoptionRecord record) {
        var gone = record.Installs.Where(i => i.State == InstallState.Left && Gone(i.Install)).Select(i => i.Install).ToList();
        if (gone.Count == 0 || !FindsBundle(evidence)) return record;

        bool Done(RecordedInstall i) => i.State == InstallState.Left && gone.Exists(g => AdoptionPreflight.SameRoot(g, i.Install));
        record = store.Update(r => r with {
            Installs = [.. r.Installs.Select(i => Done(i)
                ? i with { State = InstallState.Switched, Reason = null, HeldBy = null, HolderPid = null, HolderStartToken = null }
                : i)],
            NoticesShown = [.. r.NoticesShown.Union(gone.Select(AdoptionNotices.SwitchedKey))],
        });
        foreach (var install in gone) log("adoption_switched", AdoptionEvent.For(install, reason: RemovedByHand));
        return record;
    }

    bool Gone(OtherCliInstall install) =>
        !install.OwnedEntries.Any(e => Exists(e) || retirement.Find(e) is { } aside && Exists(aside.Aside));

    // The same PATHs preflight plans from: the login shell's, and each service's that finds a kcap at all.
    bool FindsBundle(AdoptionEvidence evidence) {
        var cli = CanonicalPath.Resolve(bundle.Cli);
        bool Bundle(string? kcap) => kcap is not null && PathComparison.Equal(CanonicalPath.Resolve(kcap), cli);

        return Bundle(evidence.KcapOnLoginPath) && evidence.Units.All(u => u.KcapOnStoredPath is null || Bundle(u.KcapOnStoredPath));
    }

    static bool Exists(string path) => new FileInfo(path).LinkTarget is not null || Path.Exists(path);

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
    // that far. A left install found left for another reason is another outcome, with a notice of its own.
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
                    if (at >= 0 && installs[at] is { State: InstallState.Left } kept && kept.Install.Identity == plan.Install.Identity
                     && kept.Reason == reason) continue;
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
