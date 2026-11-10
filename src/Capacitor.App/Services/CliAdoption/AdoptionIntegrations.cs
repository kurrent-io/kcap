using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Repoints the agent integrations at the bundle's kcap and records the cutover: the finish of the last
/// rewrite, after which a harness session that started earlier may still hold the old install's files.</summary>
internal sealed class AdoptionIntegrations(
    ApplicationIntegrationRefresh refresh, string bundleCli, AdoptionRecordStore store, TimeProvider time) {
    /// <summary>The first refresh to finish records the cutover. Once one is recorded, the refresh runs again only for
    /// a site naming an install again, and moves the cutover to its own finish only when <paramref name="repointed"/>,
    /// asked after it, says it repointed one: a site it cannot repoint would otherwise keep the cutover minutes old, and
    /// every agent the app's daemon resumed long before would hold the deletion.</summary>
    /// <param name="repointed">Null when no site names an install.</param>
    public async Task<bool> RepointAsync(Func<CancellationToken, Task<bool>>? repointed, CancellationToken ct) {
        var first = store.Load().CutoverUtc is null;
        if (!first && repointed is null) return true;
        ProcessResult result;
        try {
            result = await refresh.RunAsync(bundleCli, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: integration refresh failed: {ex}");
            return false;
        }
        if (result.TimedOut || result.ExitCode != 0) return false;
        var finished = time.GetUtcNow().UtcDateTime;
        if (first || await repointed!(ct).ConfigureAwait(false)) store.Update(r => r with { CutoverUtc = finished });
        return true;
    }
}
