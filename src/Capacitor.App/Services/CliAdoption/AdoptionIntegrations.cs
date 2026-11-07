using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Repoints the agent integrations at the bundle's kcap and records the cutover: the finish of the last
/// rewrite, after which a harness session that started earlier may still hold the old install's files.</summary>
internal sealed class AdoptionIntegrations(
    ApplicationIntegrationRefresh refresh, string bundleCli, AdoptionRecordStore store, TimeProvider time) {
    public async Task<bool> RepointAsync(bool foundNamed, CancellationToken ct) {
        if (store.Load().CutoverUtc is not null && !foundNamed) return true;
        ProcessResult result;
        try {
            result = await refresh.RunAsync(bundleCli, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: integration refresh failed: {ex}");
            return false;
        }
        if (result.TimedOut || result.ExitCode != 0) return false;
        var finished = time.GetUtcNow().UtcDateTime;
        store.Update(r => r with { CutoverUtc = finished });
        return true;
    }
}
