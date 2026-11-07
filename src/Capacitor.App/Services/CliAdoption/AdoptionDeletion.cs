using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>The last check before an install's files go, and their deletion. Each install nothing uses or names is
/// retired entry by entry and recorded switched; one that something still holds stays pending with its holder
/// recorded, so a recheck can tell whether that holder is still there without reading anything else.</summary>
internal sealed class AdoptionDeletion(
        IAdoptionMachine machine, AdoptionRecordStore store, AsideRetirement retirement, HarnessRegistry harnesses, string bundleRoot,
        Action<string, (string Key, string? Value)[]> log) {
    internal const string Changed            = "changed";
    internal const string RetireFailed       = "retire_failed";
    internal const string IdentityUnreadable = "identity_unreadable";
    internal const string EntriesUnknown     = "entries_unknown";

    /// <summary>Holds every install when the process list lacks this app's own process, which every real list holds:
    /// the list could not be read, and an empty one would say nothing runs from any install.</summary>
    internal const string ProcessesUnreadable = "processes:unreadable";

    /// <summary>Holds an install, as <c>unfinished:&lt;entry&gt;</c>, while an aside of one of its entries left by an
    /// earlier run could not be deleted; held for all when the retirement record itself could not be read.</summary>
    internal const string Unfinished = "unfinished:";

    const string PidHolder = "pid:";

    /// <param name="processes">This user's processes, read once for every install.</param>
    /// <param name="sites">Everything that can name an install, read once for every install.</param>
    /// <param name="unfinished">The entries whose interrupted retirement could not be finished; null when that is
    /// unknown.</param>
    public async Task SettleAsync(
            IReadOnlyList<OtherCliInstall> installs, IReadOnlyList<ProcessFact> processes, IReadOnlyList<ReferenceSite> sites,
            IReadOnlyList<string>? unfinished, CancellationToken ct) {
        if (installs.Count == 0 || store.Load().CutoverUtc is not { } cutover) return;

        foreach (var install in installs) {
            ct.ThrowIfCancellationRequested();
            var holder = UnfinishedEntry(install, unfinished) ?? await HolderAsync(install, cutover, processes, sites, ct).ConfigureAwait(false);
            if (holder is not null) {
                Hold(install, holder);
                continue;
            }

            // Read while the files are there: a link's old target resolves into the install only until it is gone.
            var links = store.Load().Links.Where(l => Into(l, install)).ToList();
            if (Retire(install) is { } failure) {
                Hold(install, failure);
                continue;
            }
            Switch(install, links);
        }
    }

    /// <summary>What still runs from the install or names it, or the install having changed; null when nothing
    /// holds it.</summary>
    public async Task<string?> HolderAsync(
            OtherCliInstall install, DateTime cutover, IReadOnlyList<ProcessFact> processes, IReadOnlyList<ReferenceSite> sites,
            CancellationToken ct) =>
        (processes.Any(p => p.Pid == Environment.ProcessId) ? null : ProcessesUnreadable)
     ?? AdoptionConsumers.Holder(install, cutover, processes, sites, harnesses, bundleRoot)
     ?? await ChangedAsync(install, ct).ConfigureAwait(false);

    static string? UnfinishedEntry(OtherCliInstall install, IReadOnlyList<string>? unfinished) {
        if (unfinished is null) return Unfinished + install.Root;
        var entry = install.OwnedEntries.FirstOrDefault(e => unfinished.Contains(Path.GetFullPath(e), PathComparison.Comparer));
        return entry is null ? null : Unfinished + entry;
    }

    // A reinstall at the same path is another install: preflight plans it afresh on a later launch. An install that
    // cannot be read is one being changed, as mid `npm install -g`, unless its binaries are gone already: a retirement
    // cut short deletes the entry holding them first, and is finished here.
    async Task<string?> ChangedAsync(OtherCliInstall install, CancellationToken ct) {
        if (await machine.ReadInstallAsync(install.CliBinary, ct).ConfigureAwait(false) is { } now)
            return now.Identity == install.Identity && now.Version == install.Version ? null : Changed;

        return File.Exists(install.CliBinary) || File.Exists(install.DaemonBinary) ? IdentityUnreadable : null;
    }

    // A record that lost the entries an install owns cannot say what deleting it means.
    string? Retire(OtherCliInstall install) {
        if (install.OwnedEntries.Count == 0) {
            log("adoption_retired", AdoptionEvent.For(install, "failed", EntriesUnknown));
            return EntriesUnknown;
        }
        foreach (var entry in install.OwnedEntries.OrderByDescending(e => HoldsBinaries(install, e))) {
            bool retired;
            try {
                retired = retirement.Retire(entry, aside => StillOurs(install, entry, aside));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                log("adoption_retired", AdoptionEvent.For(install, "failed", ex.Message));
                return RetireFailed;
            }
            if (!retired) {
                log("adoption_retired", AdoptionEvent.For(install, "failed", Changed));
                return Changed;
            }
        }

        log("adoption_retired", AdoptionEvent.For(install, "ok"));
        return null;
    }

    public void Hold(OtherCliInstall install, string holder) {
        int? pid = holder.StartsWith(PidHolder, StringComparison.Ordinal)
                && int.TryParse(holder.AsSpan(PidHolder.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var token = pid is { } p ? machine.StartToken(p) : null;

        store.Update(r => r with {
            Installs = [.. r.Installs.Select(i => AdoptionPreflight.SameRoot(i.Install, install)
                ? i with { HeldBy = holder, HolderPid = token is null ? null : pid, HolderStartToken = token }
                : i)],
        });
        log("adoption_kept", AdoptionEvent.For(install, reason: holder));
    }

    public void Switch(OtherCliInstall install, IReadOnlyList<LinkChange> links) {
        store.Update(r => r with {
            Installs = [.. r.Installs.Select(i => AdoptionPreflight.SameRoot(i.Install, install)
                ? i with { State = InstallState.Switched, Reason = null, HeldBy = null, HolderPid = null, HolderStartToken = null }
                : i)],
            Links = [.. r.Links.Where(l => !links.Contains(l))],
        });
        log("adoption_switched", AdoptionEvent.For(install));
    }

    static bool HoldsBinaries(OtherCliInstall install, string entry) =>
        CanonicalPath.IsWithin(install.CliBinary, entry) || CanonicalPath.IsWithin(install.DaemonBinary, entry);

    static bool Into(LinkChange link, OtherCliInstall install) {
        var target = Path.IsPathRooted(link.Original) ? link.Original : Path.Join(Path.GetDirectoryName(link.Path), link.Original);
        return install.OwnedEntries.Prepend(install.Root).Any(folder => CanonicalPath.IsWithin(target, folder));
    }

    // Checked on the renamed entry, so a tree put in its place since the identity check is never deleted: an npm
    // package must still be kcap's at the recorded version, which a reinstall at the same path changes, and a script
    // install's `current` must still name the recorded version, which a reinstall moves to its own.
    internal static bool StillOurs(OtherCliInstall install, string entry, string aside) {
        var link = new FileInfo(aside).LinkTarget is not null;
        if (install.Kind == OtherCliInstallKind.Npm)
            return !link && Package(aside) is ({ } name, { } version) && version == install.Version
                && (name == OtherCliInstallReader.NpmPackage || name.StartsWith(OtherCliInstallReader.NpmPackage + "-", StringComparison.Ordinal));

        return Path.GetFileName(entry) switch {
            ScriptInstallLayout.CurrentName  => NamesVersion(aside, install.Version),
            ScriptInstallLayout.VersionsName => !link && Directory.Exists(Path.Combine(aside, install.Version))
                && CurrentStillNames(entry, install.Version),
            _                                => !link && File.Exists(aside),
        };
    }

    // `versions` goes before `current`, so a reinstall since the identity check shows in the link still in place.
    static bool CurrentStillNames(string versions, string version) {
        var current = Path.Combine(Path.GetDirectoryName(versions)!, ScriptInstallLayout.CurrentName);
        return new FileInfo(current).LinkTarget is null && !Path.Exists(current) || NamesVersion(current, version);
    }

    static bool NamesVersion(string link, string version) =>
        new FileInfo(link).LinkTarget is { } target && Path.GetFileName(target.TrimEnd('/', '\\')) == version;

    static (string? Name, string? Version) Package(string folder) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
            return (doc.RootElement.Str("name"), doc.RootElement.Str("version"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return (null, null);
        }
    }
}
