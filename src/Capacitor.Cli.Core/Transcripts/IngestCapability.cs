using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Whether the server takes canonical transcript batches. The client carries the caller's credentials. An
/// answer holds for the process: a format, or a 404 from a server without the route. A failure is a no for now and is
/// asked again on the next call.</summary>
/// <param name="budget">How long to wait for the answer before taking it as a no; <see cref="Budget"/> when null.</param>
public sealed class IngestCapability(HttpClient client, string baseUrl, TimeProvider time, TimeSpan? budget = null) {
    /// <summary>The batch format this client sends, and the answer a server that takes it gives.</summary>
    public const int CanonicalFormat = 1;

    /// <summary>The hub's capability call, answering with <see cref="CanonicalCapabilities"/>.</summary>
    public const string HubMethod = "IngestCapabilities";

    /// <summary>How long a client waits for the answer before taking it as a no.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>The wait for a drain that runs under a cap of a few seconds: the raw post that follows a no must
    /// still fit in it. A pass capped below this wait leaves its lines spooled when the answer hangs.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    const int Unknown = 0, Supported = 1, Unsupported = 2;

    // Shared by concurrent import workers.
    int _answer;

    public async Task<bool> SupportsCanonicalAsync(CancellationToken ct) {
        if (Volatile.Read(ref _answer) is var known and not Unknown) return known == Supported;

        using var timeout = new CancellationTokenSource(budget ?? Budget, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try {
            using var resp = await client.GetAsync($"{baseUrl}/api/ingest/capabilities", linked.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return Answer(false);
            if (!resp.IsSuccessStatusCode) return false;

            var answer = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(linked.Token), CapacitorJsonContext.Default.CanonicalCapabilities);

            return Answer(answer.CanonicalFormat == CanonicalFormat);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return false;
        }
    }

    bool Answer(bool supported) {
        Volatile.Write(ref _answer, supported ? Supported : Unsupported);

        return supported;
    }
}
