using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Replaces each <c>PATH</c> location the plan found inside an install with a link to the bundle's
/// <c>kcap</c>, at its own path. Every <c>PATH</c> whose location it replaced, and the login shell's when that found no
/// <c>kcap</c>, must then resolve <c>kcap</c> to the bundle, or the links this attempt made are put back. Each link is
/// recorded, and one outside the stable folders added to the inventory, before it is made.</summary>
internal sealed class AdoptionPathStep(
        ILoginShellProbe probe, AdoptedLinkInventory inventory, AdoptionRecordStore store, ICliPathInstaller shim,
        IReadOnlyList<string> stableFolders) {
    readonly IReadOnlyList<string> _stable = [.. stableFolders.Select(CanonicalPath.Resolve)];

    /// A held plan changes nothing. A left install, or one whose links need an administrator, keeps its links here.
    public async Task<bool> ApplyAsync(AdoptionPlan plan, AdoptionEvidence evidence, string bundleCli, CancellationToken ct) {
        if (plan.HoldReason is not null) return false;

        var changes = plan.Installs.Where(p => p.Left is null && !p.NeedsAdministrator).SelectMany(p => p.Links).ToList();
        var made    = new List<LinkChange>();
        var applied = false;
        try {
            foreach (var change in changes) {
                made.Add(change);
                Swap(change);
            }

            if (plan.NeedsStableLink && await shim.InstallAsync(bundleCli, ct).ConfigureAwait(false) is { Outcome: ShimOutcome.Failed } failed) {
                Console.Error.WriteLine($"kcap: could not make the stable kcap link: {failed.Detail}");
                return false;
            }

            applied = await ResolvesToBundleAsync(evidence, changes, plan.NeedsStableLink && evidence.KcapOnLoginPath is null, bundleCli, ct)
                .ConfigureAwait(false);
            return applied;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            Console.Error.WriteLine($"kcap: could not replace the kcap PATH links: {ex.Message}");
            return false;
        } finally {
            if (!applied) PutBack(made, CancellationToken.None);
        }
    }

    /// Whether every PATH whose location <paramref name="changes"/> replaced now finds the bundle's <c>kcap</c>.
    public Task<bool> FindsBundleAsync(AdoptionEvidence evidence, IReadOnlyList<LinkChange> changes, string bundleCli, CancellationToken ct) =>
        ResolvesToBundleAsync(evidence, changes, loginRegardless: false, bundleCli, ct);

    /// Adds each link outside the stable folders to the inventory, for a step that makes the links itself.
    public void Adopt(IReadOnlyList<LinkChange> links) {
        foreach (var link in links.Where(l => !InStableFolder(l.Path))) inventory.Add(link.Path);
    }

    /// Takes back out of the inventory each adopted link that no longer points at the bundle.
    public void Release(IReadOnlyList<LinkChange> links) {
        foreach (var link in links.Where(l => !InStableFolder(l.Path) && new FileInfo(l.Path).LinkTarget != l.Replacement))
            inventory.Remove(link.Path);
    }

    /// Restores every recorded link, for a rollback or a crash: an earlier attempt's links included. Safe to run again.
    public Task PutBackAsync(CancellationToken ct) {
        PutBack(store.Load().Links, ct);
        return Task.CompletedTask;
    }

    /// Only a link that still points at the bundle it was given goes back, and only to an old target that still
    /// exists: one changed since, such as a link a bundle move carried along, or one whose install is gone, is left as
    /// it is and stays recorded.
    void PutBack(IReadOnlyList<LinkChange> changes, CancellationToken ct) {
        foreach (var change in changes) {
            ct.ThrowIfCancellationRequested();
            try {
                if (new FileInfo(change.Path).LinkTarget != change.Replacement) continue;
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(change.Path)!, change.Original))) {
                    Console.Error.WriteLine($"kcap: left {change.Path} on the app's kcap: {change.Original} is gone");
                    continue;
                }
                KcapLinkSwap.Replace(change.Path, change.Replacement, change.Original);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: could not put {change.Path} back: {ex.Message}");
                continue;
            }

            store.Update(r => r with { Links = [.. r.Links.Where(l => l != change)] });
            if (!InStableFolder(change.Path)) inventory.Remove(change.Path);
        }
    }

    void Swap(LinkChange change) {
        var key = AdoptionPreflight.LinkKey(change.Path);
        store.Update(r => r with { Links = [.. r.Links.Where(l => !PathComparison.Equal(AdoptionPreflight.LinkKey(l.Path), key)), change] });
        if (!InStableFolder(change.Path)) inventory.Add(change.Path);
        KcapLinkSwap.Replace(change.Path, change.Original, change.Replacement);
    }

    bool InStableFolder(string link) => _stable.Contains(CanonicalPath.Resolve(Path.GetDirectoryName(link)!), PathComparison.Comparer);

    async Task<bool> ResolvesToBundleAsync(
            AdoptionEvidence evidence, IReadOnlyList<LinkChange> changes, bool loginRegardless, string bundleCli, CancellationToken ct) {
        var bundle   = CanonicalPath.Resolve(bundleCli);
        var replaced = changes.Select(c => AdoptionPreflight.LinkKey(c.Path)).ToHashSet(PathComparison.Comparer);

        bool Replaced(string? location) => location is not null && replaced.Contains(AdoptionPreflight.LinkKey(location));

        bool FindsBundle(string? kcap, string where) {
            if (kcap is not null && PathComparison.Equal(CanonicalPath.Resolve(kcap), bundle)) return true;
            Console.Error.WriteLine($"kcap: {where} finds kcap at {kcap ?? "nowhere"}, not the app's, once the PATH links were replaced");
            return false;
        }

        foreach (var unit in evidence.Units.Where(u => Replaced(u.KcapOnStoredPath))) {
            if (!FindsBundle(BinaryProbe.Searching(unit.StoredPath).Resolve("kcap"), $"the PATH of service '{unit.Name}'")) return false;
        }

        var login = Replaced(evidence.KcapOnLoginPath) || loginRegardless;
        return !login || FindsBundle(await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false), "the login shell's PATH");
    }
}
