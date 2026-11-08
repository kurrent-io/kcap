using System.Security.Cryptography;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Switches a root-owned install behind one administrator prompt, after every step the user can take alone:
/// the helper swaps the install's PATH links, the PATH and the deletion checks run again as this user, and only their
/// pass lets it delete. A launch settles a helper an earlier one left before anything else reads or deletes.</summary>
internal sealed class AdoptionPrivilegedStep(
        IAdoptionMachine machine, AdoptionRecordStore store, AsideRetirement retirement, AdoptionDeletion deletion,
        AdoptionPathStep path, IPrivilegedSwap administrator, Func<LifecyclePrompt, CancellationToken, Task<bool>> confirm,
        AdoptionBundle bundle, string helperFolder, HomeSites homeSites, Action<string, (string Key, string? Value)[]> log) {
    internal const string PathNotOnBundle = "path_not_on_bundle";
    internal const string NothingLeft     = "nothing_left";
    internal const string HelperRunning   = "helper_running";

    // Installs this session already explained; any other is explained before its prompt, Finish switching's included.
    readonly HashSet<string> _explained = new(PathComparison.Comparer);

    /// The helper waits this long for go, which follows the login shell's PATH probe and every deletion check.
    internal static readonly TimeSpan GoWait = TimeSpan.FromMinutes(2);

    /// How long a later launch waits for a helper that may still be deleting after go.
    internal static readonly TimeSpan SettleWait = GoWait + TimeSpan.FromMinutes(1);

    /// <summary>Settles the helper run the record names, which a quit or crash left unfinished: waits for it to exit,
    /// then reads the files as <see cref="SettleFromFiles"/> does, without a prompt. One that may still be running past
    /// the wait keeps its record: its files are half done, and a second helper would race it.</summary>
    /// <returns>False while that helper still runs.</returns>
    public async Task<bool> SettleAsync(CancellationToken ct) {
        if (store.Load().Privileged is not { } helper) return true;
        if (helper.Pid is { } pid && !await machine.WaitForExitAsync(pid, helper.StartToken, SettleWait, ct).ConfigureAwait(false)) {
            log("adoption_privileged", [("install", helper.InstallRoot), ("result", "unsettled"), ("reason", HelperRunning)]);
            return false;
        }

        var root = CanonicalPath.Resolve(helper.InstallRoot);
        var settled = store.Load().Installs.FirstOrDefault(i =>
            i.State == InstallState.Pending && PathComparison.Equal(CanonicalPath.Resolve(i.Install.Root), root));
        if (settled is { Install: var install }) SettleFromFiles(install, helper.GoSent);
        store.Update(r => r with { Privileged = null });
        return true;
    }

    /// <summary>Asks nothing once none of the install is left, which switches it, nor while the app runs from
    /// ~/Applications or something still holds the install. The helper swaps <paramref name="plan"/>'s links, which are
    /// the ones this launch found still finding the install.</summary>
    /// <param name="unfinished">Entries whose aside an earlier run could not delete: the helper deletes them; null when
    /// the retirement record could not be read, which holds the install.</param>
    /// <remarks>The first time this session, says why the password is needed before the prompt asks for it, and leaves
    /// the install as a cancelled prompt does when the user declines: unasked, macOS shows a prompt that names only a
    /// script.</remarks>
    /// <returns>True when a helper cut short left every entry in place: pending, with nothing this session's other runs
    /// would prompt for.</returns>
    public async Task<PrivilegedStepOutcome> RunAsync(
            OtherCliInstall install, InstallPlan? plan, AdoptionEvidence evidence, IReadOnlyList<string>? unfinished,
            CancellationToken ct) {
        if (store.Load().CutoverUtc is not { } cutover) return PrivilegedStepOutcome.Settled;

        var links   = plan?.Links ?? [];
        var entries = Entries(install);
        if (links.Count == 0 && !entries.Any(e => Exists(e) || retirement.HasAside(e))) {
            log("adoption_retired", AdoptionEvent.For(install, "ok", NothingLeft));
            deletion.Switch(install, []);
            return PrivilegedStepOutcome.Settled;
        }
        if (!bundle.InSystemApplications) {
            Leave(install, LeftReason.SharedInstallPrivateApp);
            return PrivilegedStepOutcome.Settled;
        }
        if (store.Load().Privileged is not null) {
            deletion.Hold(install, HelperRunning);
            return PrivilegedStepOutcome.Settled;
        }
        if (await HolderAsync(install, cutover, unfinished, ct).ConfigureAwait(false) is { } holder) {
            deletion.Hold(install, holder);
            return PrivilegedStepOutcome.Settled;
        }
        if (_explained.Add(CanonicalPath.Resolve(install.Root)) && !await confirm(Explanation(install), ct).ConfigureAwait(false)) {
            log("adoption_privileged", AdoptionEvent.For(install, "declined"));
            Leave(install, LeftReason.PromptCancelled);
            return PrivilegedStepOutcome.Declined;
        }

        var planned = new List<PrivilegedEntry>();
        foreach (var e in entries) planned.Add(await PinnedAsync(install, Entry(install, retirement.Plan(e)), ct).ConfigureAwait(false));
        var folder = Path.Combine(Path.GetFullPath(helperFolder), "cli-adoption-helper-" + Guid.NewGuid().ToString("N"));
        var spec   = new PrivilegedSpec(links, planned,
            Path.Combine(folder, "control"), Path.Combine(folder, "status"), GoWait, RandomNumberGenerator.GetHexString(32, lowercase: true)) {
            Sites   = homeSites,
            Needles = AdoptionConsumers.Needles(entries),
        };
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
            PrivilegedResult.InUse     => "in_use",
            _                          => "cut_short",
        }));

        var outcome = PrivilegedStepOutcome.Settled;
        switch (result) {
            case PrivilegedResult.Deleted:
                deletion.Switch(install, []);
                break;
            case PrivilegedResult.PutBack:
                path.Release(links);
                // Put back for a PATH that stopped finding the bundle: nothing holds it for a later run to see go.
                if (store.Load().Installs.FirstOrDefault(i => AdoptionPreflight.SameRoot(i.Install, install)) is
                    { State: InstallState.Pending, HeldBy: null })
                    outcome = PrivilegedStepOutcome.WaitsOnPrompt;
                break;
            case PrivilegedResult.Cancelled:
                path.Release(links);
                Leave(install, LeftReason.PromptCancelled);
                outcome = PrivilegedStepOutcome.Declined;
                break;
            case PrivilegedResult.InUse:
                path.Release(links);
                Leave(install, LeftReason.SharedInstallInUse);
                break;
            case PrivilegedResult.CutShort:
                if (SettleFromFiles(install, store.Load().Privileged?.GoSent ?? false) == InstallState.Pending) {
                    path.Release(links);
                    outcome = PrivilegedStepOutcome.WaitsOnPrompt;
                }
                break;
        }
        store.Update(r => r with { Privileged = null });
        return outcome;
    }

    /// <summary>What a helper that ended without its outcome left, read from the files. Every entry in place, with
    /// nothing set aside: pending, a working state if its links moved. Nothing left: switched. Anything between can be
    /// finished only as root, so it is left for Finish switching. Before go the helper sets nothing aside, so whatever
    /// was set aside then is an earlier run's, and needs root all the same.</summary>
    InstallState SettleFromFiles(OtherCliInstall install, bool goSent) {
        var entries = Entries(install);
        var asides  = entries.Count(retirement.HasAside);
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

    // macOS shows its own wording in the administrator prompt, which may leave out Prompt's text altogether.
    internal static LifecyclePrompt Explanation(OtherCliInstall install) => new(
        LifecyclePrompt.KindRemoveSharedInstall, null, null, false,
        $"kcap now uses the app's own command-line tool. The {AdoptionNotices.Name(install)} at {install.Root} serves every "
      + "account on this Mac, so removing it takes an administrator password. macOS asks for it next, for a script started by "
      + "kcap that points the kcap command at the app and removes that install. Not now keeps it until you choose "
      + $"{AdoptionNotices.FinishSwitching} in the menu bar.");

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

    // An npm package folder must still hold its own package.json, naming that package at the recorded version. The
    // folder holding the daemon must hold the very binary preflight read, which a reinstall of the same version
    // replaces, and a script install's current must still name its version, which an update moves on.
    internal static PrivilegedEntry Entry(OtherCliInstall install, AsideEntry aside) {
        (string, string)? daemon = Directory.Exists(aside.Original) && new FileInfo(aside.Original).LinkTarget is null
                && CanonicalPath.IsWithin(install.DaemonBinary, aside.Original)
            ? (Path.GetRelativePath(CanonicalPath.Resolve(aside.Original), CanonicalPath.Resolve(install.DaemonBinary)), install.Identity)
            : null;
        var entry = install.Kind == OtherCliInstallKind.Script
            ? Path.GetFileName(aside.Original) switch {
                ScriptInstallLayout.VersionsName => new PrivilegedEntry(aside, PrivilegedEntryKind.Directory, null, [], daemon),
                ScriptInstallLayout.CurrentName  => new PrivilegedEntry(aside, PrivilegedEntryKind.Link, null, [], LinkTargetName: install.Version),
                _                                => new PrivilegedEntry(aside, PrivilegedEntryKind.File, null, []),
            }
            : new PrivilegedEntry(aside, PrivilegedEntryKind.Directory, "package.json",
                [Quoted("@kurrent/" + Path.GetFileName(aside.Original)), Quoted(install.Version)], daemon);
        return entry with {
            Present  = Exists(aside.Original) || Exists(aside.Aside),
            Children = Path.GetFileName(aside.Original) == ScriptInstallLayout.VersionsName && install.Kind == OtherCliInstallKind.Script
                ? Listing(Directory.Exists(aside.Original) ? aside.Original : aside.Aside)
                : null,
        };
    }

    // A same-version reinstall passes the name and version check: an npm package the daemon does not pin is pinned by
    // its package.json as it is before the prompt.
    async Task<PrivilegedEntry> PinnedAsync(OtherCliInstall install, PrivilegedEntry entry, CancellationToken ct) {
        if (install.Kind != OtherCliInstallKind.Npm || entry.Pinned is not null) return entry;
        var folder = Directory.Exists(entry.Entry.Original) ? entry.Entry.Original : entry.Entry.Aside;
        return await machine.IdentityAsync(Path.Combine(folder, "package.json"), ct).ConfigureAwait(false) is { } identity
            ? entry with { Pinned = ("package.json", identity) }
            : entry;
    }

    static string Quoted(string value) => $"\"{value}\"";

    // An update stages its version inside versions before it moves current there. One that cannot be listed expects
    // nothing, which moves the install back rather than deleting what it could not see.
    static IReadOnlyList<string> Listing(string folder) {
        try {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal)] : [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static bool Exists(string path) => new FileInfo(path).LinkTarget is not null || Path.Exists(path);

    static void RemoveFolder(string folder) {
        try {
            Directory.Delete(folder);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            if (Directory.Exists(folder)) Console.Error.WriteLine($"kcap: could not remove {folder}: {ex.Message}");
        }
    }
}
