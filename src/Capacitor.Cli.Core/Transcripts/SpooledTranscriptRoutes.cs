namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The canonical routes of one spool drain pass, one per stream. A stream's route projects its transcript up to
/// the first line of the stream's first batch in the pass.</summary>
/// <param name="replayBudget">The most opening a route may take. One that cannot finish sends the stream raw, which a
/// server always takes, rather than leave its tail, and the session's end behind it, to a pass that would run out the
/// same way.</param>
public sealed class SpooledTranscriptRoutes(HttpClient client, string baseUrl, TimeSpan replayBudget, TimeProvider time) {
    readonly IngestCapability _capability = new(client, baseUrl, time);

    readonly Dictionary<(string SessionId, string? AgentId), (CanonicalRoute? Route, int Next)> _streams = [];

    /// <summary>The route for a spooled batch, or null when it goes raw as spooled. A batch that carries no lines goes
    /// raw: there is nothing to project.</summary>
    public async Task<CanonicalRoute?> ForAsync(TranscriptBatch batch, CancellationToken ct) {
        if (!CanonicalVendors.Contains(batch.Vendor) || batch.LineNumbers is not { Length: > 0 } numbers) return null;

        var key = (batch.SessionId, batch.AgentId);

        // A batch that starts at or below a line the route projected opens the stream again: a line projected twice
        // leaves the state an uninterrupted run never has.
        if (!_streams.TryGetValue(key, out var stream) || numbers[0] < stream.Next) stream = (await OpenAsync(batch, numbers[0], ct), 0);

        _streams[key] = stream with { Next = numbers[^1] + 1 };

        return stream.Route;
    }

    async Task<CanonicalRoute?> OpenAsync(TranscriptBatch batch, int firstLine, CancellationToken ct) {
        using var budget = new CancellationTokenSource(replayBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try {
            return await TranscriptPost.RouteAsync(_capability, batch.Vendor, batch.SessionId, batch.AgentId, batch.TranscriptPath, firstLine, linked.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return null;
        }
    }
}
