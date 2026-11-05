using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Switches a root-owned install behind one administrator prompt, after every step the user can take alone:
/// the helper swaps the install's PATH links, the PATH and the deletion checks run again as this user, and only their
/// pass lets it delete. A launch settles a helper an earlier one left before anything else reads or deletes.</summary>
internal sealed class AdoptionPrivilegedStep(
        IAdoptionMachine machine, AdoptionRecordStore store, AsideRetirement retirement, AdoptionDeletion deletion,
        AdoptionPathStep path, IPrivilegedSwap administrator, AdoptionBundle bundle, string helperFolder,
        Action<string, (string Key, string? Value)[]> log) {
    internal const string PathNotOnBundle = "path_not_on_bundle";
    internal const string NothingLeft     = "nothing_left";

    /// The helper waits this long for go, which follows the login shell's PATH probe and every deletion check.
    internal static readonly TimeSpan GoWait = TimeSpan.FromMinutes(2);

    /// How long a later launch waits for a helper that may still be deleting after go.
    internal static readonly TimeSpan SettleWait = GoWait + TimeSpan.FromMinutes(1);

    /// <summary>Settles the helper run the record names, which a quit or crash left unfinished: waits for it to exit,
    /// then reads the files as <see cref="SettleFromFiles"/> does, without a prompt.</summary>
    public async Task SettleAsync(CancellationToken ct) {
        if (store.Load().Privileged is not { } helper) return;
        if (helper.Pid is { } pid) await machine.WaitForExitAsync(pid, helper.StartToken, SettleWait, ct).ConfigureAwait(false);

        var root = CanonicalPath.Resolve(helper.InstallRoot);
        var settled = store.Load().Installs.FirstOrDefault(i =>
            i.State == InstallState.Pending && PathComparison.Equal(CanonicalPath.Resolve(i.Install.Root), root));
        if (settled is { Install: var install }) SettleFromFiles(install, helper.GoSent);
        store.Update(r => r with { Privileged = null });
    }

    /// <summary>Asks nothing while something still holds the install, and nothing at all once none of it is left. The
    /// helper swaps <paramref name="plan"/>'s links, which are the ones this launch found still finding the install.</summary>
    /// <param name="unfinished">Entries whose aside an earlier run could not delete: the helper deletes them; null when
    /// the retirement record could not be read, which holds the install.</param>
    public async Task RunAsync(
            OtherCliInstall install, InstallPlan? plan, AdoptionEvidence evidence, IReadOnlyList<string>? unfinished, CancellationToken ct) {
        if (store.Load().CutoverUtc is not { } cutover) return;
        if (!bundle.InSystemApplications) {
            Leave(install, LeftReason.SharedInstallPrivateApp);
            return;
        }
        if (await HolderAsync(install, cutover, unfinished, ct).ConfigureAwait(false) is { } holder) {
            deletion.Hold(install, holder);
            return;
        }

        var links   = plan?.Links ?? [];
        var entries = Entries(install);
        if (links.Count == 0 && !entries.Any(e => Exists(e) || retirement.Find(e) is { } aside && Exists(aside.Aside))) {
            log("adoption_retired", AdoptionEvent.For(install, "ok", NothingLeft));
            deletion.Switch(install, []);
            return;
        }

        var folder = Path.Combine(Path.GetFullPath(helperFolder), "cli-adoption-helper-" + Guid.NewGuid().ToString("N"));
        var spec   = new PrivilegedSpec(links, [.. entries.Select(e => Entry(install, retirement.Plan(e)))],
            Path.Combine(folder, "control"), Path.Combine(folder, "status"), GoWait);
        path.Adopt(links);
        PrivilegedResult result;
        try {
            result = await administrator.RunAsync(install.Root, spec, Prompt(install),
                c => VerifyAsync(install, cutover, evidence, links, unfinished, c), ct).ConfigureAwait(false);
        } finally {
            RemoveFolder(folder);
        }
        log("adoption_privileged", AdoptionEvent.For(install, result switch {
            PrivilegedResult.Deleted   => "deleted",
            PrivilegedResult.PutBack   => "put_back",
            PrivilegedResult.Cancelled => "cancelled",
            _                          => "cut_short",
        }));

        switch (result) {
            case PrivilegedResult.Deleted:
                deletion.Switch(install, []);
                break;
            case PrivilegedResult.PutBack:
                path.Release(links);
                break;
            case PrivilegedResult.Cancelled:
                path.Release(links);
                Leave(install, LeftReason.PromptCancelled);
                break;
            case PrivilegedResult.CutShort:
                if (SettleFromFiles(install, store.Load().Privileged?.GoSent ?? false) == InstallState.Pending) path.Release(links);
                break;
        }
        store.Update(r => r with { Privileged = null });
    }

    /// <summary>What a helper that ended without its outcome left, read from the files. Every entry in place, with
    /// nothing set aside: pending, a working state if its links moved. Nothing left: switched. Anything between can be
    /// finished only as root, so it is left for Finish switching. Before go the helper sets nothing aside, so whatever
    /// was set aside then is an earlier run's, and needs root all the same.</summary>
    InstallState SettleFromFiles(OtherCliInstall install, bool goSent) {
        var entries = Entries(install);
        var asides  = entries.Count(e => retirement.Find(e) is { } aside && Exists(aside.Aside));
        var present = entries.Count(Exists);
        var goField = ("go_sent", goSent ? "true" : "false");
        if (asides == 0 && present == entries.Count) {
            log("adoption_privileged", [.. AdoptionEvent.For(install, "settled", "in_place"), goField]);
            return InstallState.Pending;
        }
        if (asides == 0 && present == 0) {
            log("adoption_privileged", [.. AdoptionEvent.For(install, "settled", "deleted"), goField]);
            deletion.Switch(install, []);
            return InstallState.Switched;
        }
        log("adoption_privileged", [.. AdoptionEvent.For(install, "settled", "cut_short"), goField]);
        Leave(install, LeftReason.DeletionCutShort);
        return InstallState.Left;
    }

    internal static string Prompt(OtherCliInstall install) =>
        $"kcap wants to remove the {AdoptionNotices.Name(install)} at {install.Root} and use its own command-line tool instead. "
      + "Every account on this Mac shares that install.";

    // Anything could have changed while the prompt was open: a daemon started from the install, an integration
    // restored, a session begun.
    async Task<bool> VerifyAsync(
            OtherCliInstall install, DateTime cutover, AdoptionEvidence evidence, IReadOnlyList<LinkChange> links,
            IReadOnlyList<string>? unfinished, CancellationToken ct) {
        if (!await path.FindsBundleAsync(evidence, links, bundle.Cli, ct).ConfigureAwait(false)) {
            log("adoption_kept", AdoptionEvent.For(install, reason: PathNotOnBundle));
            return false;
        }
        if (await HolderAsync(install, cutover, unfinished, ct).ConfigureAwait(false) is { } holder) {
            deletion.Hold(install, holder);
            return false;
        }
        return true;
    }

    // An aside of this install that an earlier run could not delete is for the helper to delete, so it holds nothing.
    async Task<string?> HolderAsync(OtherCliInstall install, DateTime cutover, IReadOnlyList<string>? unfinished, CancellationToken ct) {
        if (unfinished is null) return AdoptionDeletion.Unfinished + install.Root;
        var processes = machine.ReadProcesses();
        var sites     = await machine.ReadSitesAsync(ct).ConfigureAwait(false);
        return await deletion.HolderAsync(install, cutover, processes, sites, ct).ConfigureAwait(false);
    }

    void Leave(OtherCliInstall install, LeftReason reason) {
        store.Update(r => r with {
            Installs = [.. r.Installs.Select(i => AdoptionPreflight.SameRoot(i.Install, install)
                ? i with { State = InstallState.Left, Reason = reason, HeldBy = null, HolderPid = null, HolderStartToken = null }
                : i)],
        });
        log("adoption_left", AdoptionEvent.For(install, reason: reason.ToString()));
    }

    // A script install's entries come from its root alone: nothing else may reach root without a marker.
    static IReadOnlyList<string> Entries(OtherCliInstall install) => install.Kind == OtherCliInstallKind.Script
        ? [
            Path.Combine(install.Root, ScriptInstallLayout.VersionsName),
            Path.Combine(install.Root, ScriptInstallLayout.CurrentName),
            Path.Combine(install.Root, ScriptInstallLayout.MarkerFileName),
        ]
        : install.OwnedEntries;

    // An npm package folder must still hold its own package.json, naming that package at the recorded version.
    static PrivilegedEntry Entry(OtherCliInstall install, AsideEntry aside) => install.Kind == OtherCliInstallKind.Script
        ? new PrivilegedEntry(aside, Path.GetFileName(aside.Original) switch {
            ScriptInstallLayout.VersionsName => PrivilegedEntryKind.Directory,
            ScriptInstallLayout.CurrentName  => PrivilegedEntryKind.Link,
            _                                => PrivilegedEntryKind.File,
        }, null, [])
        : new PrivilegedEntry(aside, PrivilegedEntryKind.Directory, "package.json",
            [Quoted("@kurrent/" + Path.GetFileName(aside.Original)), Quoted(install.Version)]);

    static string Quoted(string value) => $"\"{value}\"";

    static bool Exists(string path) => new FileInfo(path).LinkTarget is not null || Path.Exists(path);

    static void RemoveFolder(string folder) {
        try {
            Directory.Delete(folder);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            if (Directory.Exists(folder)) Console.Error.WriteLine($"kcap: could not remove {folder}: {ex.Message}");
        }
    }
}
