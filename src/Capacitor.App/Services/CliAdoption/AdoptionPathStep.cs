using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Replaces each <c>PATH</c> location the plan found inside an install with a link to the bundle's
/// <c>kcap</c>, at its own path. Every <c>PATH</c> whose location it replaced, and the login shell's when that found no
/// <c>kcap</c>, must then resolve <c>kcap</c> to the bundle, or every recorded link is put back. Each link is recorded,
/// and one outside the stable folders added to the inventory, before it is made.</summary>
internal sealed class AdoptionPathStep(
        ILoginShellProbe probe, AdoptedLinkInventory inventory, AdoptionRecordStore store, ICliPathInstaller shim,
        IReadOnlyList<string> stableFolders) {
    readonly IReadOnlyList<string> _stable = [.. stableFolders.Select(CanonicalPath.Resolve)];

    /// A held plan changes nothing. A left install, or one whose links need an administrator, keeps its links here.
    public async Task<bool> ApplyAsync(AdoptionPlan plan, AdoptionEvidence evidence, string bundleCli, CancellationToken ct) {
        if (plan.HoldReason is not null) return false;

        var changes = plan.Installs.Where(p => p.Left is null && !p.NeedsAdministrator).SelectMany(p => p.Links).ToList();
        var applied = false;
        try {
            foreach (var change in changes) Swap(change);

            if (plan.NeedsStableLink && await shim.InstallAsync(bundleCli, ct).ConfigureAwait(false) is { Outcome: ShimOutcome.Failed } failed) {
                Console.Error.WriteLine($"kcap: could not make the stable kcap link: {failed.Detail}");
                return false;
            }

            applied = await ResolvesToBundleAsync(plan, evidence, changes, bundleCli, ct).ConfigureAwait(false);
            return applied;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            Console.Error.WriteLine($"kcap: could not replace the kcap PATH links: {ex.Message}");
            return false;
        } finally {
            if (!applied) await PutBackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// Restores each recorded link that still points at the bundle it was given. One changed since, such as a link a
    /// bundle move carried along, is left as it is and stays recorded. Safe to run again, and after a crash.
    public Task PutBackAsync(CancellationToken ct) {
        foreach (var change in store.Load().Links) {
            ct.ThrowIfCancellationRequested();
            try {
                if (new FileInfo(change.Path).LinkTarget != change.Replacement) continue;
                KcapLinkSwap.Replace(change.Path, change.Replacement, change.Original);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: could not put {change.Path} back: {ex.Message}");
                continue;
            }

            store.Update(r => r with { Links = [.. r.Links.Where(l => l != change)] });
            if (!InStableFolder(change.Path)) inventory.Remove(change.Path);
        }

        return Task.CompletedTask;
    }

    void Swap(LinkChange change) {
        var key = AdoptionPreflight.LinkKey(change.Path);
        store.Update(r => r with { Links = [.. r.Links.Where(l => !PathComparison.Equal(AdoptionPreflight.LinkKey(l.Path), key)), change] });
        if (!InStableFolder(change.Path)) inventory.Add(change.Path);
        KcapLinkSwap.Replace(change.Path, change.Original, change.Replacement);
    }

    bool InStableFolder(string link) => _stable.Contains(CanonicalPath.Resolve(Path.GetDirectoryName(link)!), PathComparison.Comparer);

    async Task<bool> ResolvesToBundleAsync(
            AdoptionPlan plan, AdoptionEvidence evidence, IReadOnlyList<LinkChange> changes, string bundleCli, CancellationToken ct) {
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

        var login = Replaced(evidence.KcapOnLoginPath) || (plan.NeedsStableLink && evidence.KcapOnLoginPath is null);
        return !login || FindsBundle(await probe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false), "the login shell's PATH");
    }
}
