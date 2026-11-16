using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Whether the server takes canonical transcript batches. The client carries the caller's credentials. A
/// positive answer holds for the process; a 404 or a fault is a no for now and is asked again on the next call.</summary>
public sealed class IngestCapability(HttpClient client, string baseUrl, TimeProvider time) {
    const int          CanonicalFormat = 1;
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    bool _supported;

    public async Task<bool> SupportsCanonicalAsync(CancellationToken ct) {
        if (_supported) return true;

        using var timeout = new CancellationTokenSource(Budget, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try {
            using var resp = await client.GetAsync($"{baseUrl}/api/ingest/capabilities", linked.Token);
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(linked.Token));

            return _supported = doc.RootElement.Num("canonical_format") == CanonicalFormat;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return false;
        }
    }
}
