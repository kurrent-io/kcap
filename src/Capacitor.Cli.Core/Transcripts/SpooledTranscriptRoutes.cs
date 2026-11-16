namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The canonical routes of one spool drain pass, one per stream. A stream's route projects its transcript up to
/// the first line of the stream's first batch in the pass.</summary>
/// <param name="replayBudget">The most a stream's replay may take. One that cannot finish sends the stream raw, which a
/// server always takes, rather than leave its tail, and the session's end behind it, to a pass that would run out the
/// same way.</param>
/// <param name="decide">The redaction that captured the spooled tails, for a replay to decide a large line with.</param>
public sealed class SpooledTranscriptRoutes(HttpClient client, string baseUrl, TimeSpan replayBudget, TimeProvider time, LineCaptureDecider? decide = null) {
    readonly IngestCapability _capability = new(client, baseUrl, time, IngestCapability.DrainBudget);

    readonly Dictionary<(string SessionId, string? AgentId), (CanonicalRoute? Route, int Next)> _streams = [];

    /// <summary>The route for a spooled batch, or null when it goes raw as spooled. A batch that carries no lines goes
    /// raw: there is nothing to project.</summary>
    public async Task<CanonicalRoute?> ForAsync(TranscriptBatch batch, CancellationToken ct) {
        if (!CanonicalVendors.Contains(batch.Vendor) || batch.LineNumbers is not { Length: > 0 } numbers) return null;

        var key = (batch.SessionId, batch.AgentId);

        // A batch that does not start where the route left off opens the stream again: the route has either projected
        // its lines already or not seen the ones before it.
        if (!_streams.TryGetValue(key, out var stream) || numbers[0] != stream.Next)
            stream = (await TranscriptPost.RouteAsync(_capability, batch.Vendor, batch.SessionId, batch.AgentId, batch.TranscriptPath, numbers[0], replayBudget, decide, time, ct), 0);

        _streams[key] = stream with { Next = numbers[^1] + 1 };

        return stream.Route;
    }
}
