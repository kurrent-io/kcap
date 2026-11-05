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
    internal const string Changed      = "changed";
    internal const string RetireFailed = "retire_failed";

    const string PidHolder = "pid:";

    /// <param name="processes">This user's processes, read once for every install.</param>
    /// <param name="sites">Everything that can name an install, read once for every install.</param>
    public async Task SettleAsync(
            IReadOnlyList<OtherCliInstall> installs, IReadOnlyList<ProcessFact> processes, IReadOnlyList<ReferenceSite> sites,
            CancellationToken ct) {
        if (installs.Count == 0 || store.Load().CutoverUtc is not { } cutover) return;

        foreach (var install in installs) {
            ct.ThrowIfCancellationRequested();
            var holder = AdoptionConsumers.Holder(install, cutover, processes, sites, harnesses, bundleRoot)
                      ?? await ChangedAsync(install, ct).ConfigureAwait(false);
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

    // A reinstall at the same path is another install: preflight plans it afresh on a later launch.
    async Task<string?> ChangedAsync(OtherCliInstall install, CancellationToken ct) =>
        await machine.ReadInstallAsync(install.CliBinary, ct).ConfigureAwait(false) is { } now && now.Identity != install.Identity
            ? Changed
            : null;

    string? Retire(OtherCliInstall install) {
        foreach (var entry in install.OwnedEntries) {
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

    void Hold(OtherCliInstall install, string holder) {
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

    void Switch(OtherCliInstall install, List<LinkChange> links) {
        store.Update(r => r with {
            Installs = [.. r.Installs.Select(i => AdoptionPreflight.SameRoot(i.Install, install)
                ? i with { State = InstallState.Switched, Reason = null, HeldBy = null, HolderPid = null, HolderStartToken = null }
                : i)],
            Links = [.. r.Links.Where(l => !links.Contains(l))],
        });
        log("adoption_switched", AdoptionEvent.For(install));
    }

    static bool Into(LinkChange link, OtherCliInstall install) {
        var target = Path.IsPathRooted(link.Original) ? link.Original : Path.Join(Path.GetDirectoryName(link.Path), link.Original);
        return install.OwnedEntries.Prepend(install.Root).Any(folder => CanonicalPath.IsWithin(target, folder));
    }

    // Checked on the renamed entry, so a tree put in its place between the identity check and the rename is not deleted.
    static bool StillOurs(OtherCliInstall install, string entry, string aside) {
        var link = new FileInfo(aside).LinkTarget is not null;
        if (install.Kind == OtherCliInstallKind.Npm)
            return !link && PackageName(aside) is { } name
                && (name == OtherCliInstallReader.NpmPackage || name.StartsWith(OtherCliInstallReader.NpmPackage + "-", StringComparison.Ordinal));

        return Path.GetFileName(entry) switch {
            ScriptInstallLayout.CurrentName  => link,
            ScriptInstallLayout.VersionsName => !link && Directory.Exists(aside),
            _                                => !link && File.Exists(aside),
        };
    }

    static string? PackageName(string folder) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
            return doc.RootElement.Str("name");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }
}
