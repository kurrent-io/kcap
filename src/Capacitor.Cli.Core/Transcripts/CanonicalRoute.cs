namespace Capacitor.Cli.Core.Transcripts;

/// <summary>One transcript source's path to the canonical ingress. It owns the assembler whose projection state
/// follows the source's lines, so every batch of the source goes through one route, or the source falls back to raw
/// lines for good.</summary>
public sealed class CanonicalRoute {
    public const string HttpPath  = "/hooks/transcript/canonical";
    public const string HubMethod = "SendCanonicalBatch";

    readonly CanonicalBatchAssembler _assembler;

    int[]?                    _lastNumbers;
    CanonicalTranscriptBatch? _last;

    CanonicalRoute(CanonicalBatchAssembler assembler) => _assembler = assembler;

    /// <summary>The route for a source, or null when its lines go raw: a vendor that is not canonical, or a server
    /// that cannot take canonical batches.</summary>
    public static CanonicalRoute? For(string? vendor, string sessionId, string? agentId, bool serverSupports) {
        if (!serverSupports || !CanonicalVendors.Contains(vendor)) return null;

        var name = vendor ?? "claude";
        if (TranscriptProjection.For(name) is not { } projection || TranscriptSignals.For(name) is not { } signals) return null;

        return new(new(projection, signals, sessionId, agentId, name));
    }

    public bool Active { get; private set; } = true;

    /// <summary>Projects the lines the server already holds, so the batches after them project as one uninterrupted
    /// run would. Lines are the agent's raw ones, read without denying it the file. A replay that fails leaves the
    /// projection half-advanced, so the route falls back.</summary>
    /// <returns>Whether every line before the bound was there to project. A file that is missing or shorter replays
    /// what it has, and the batches after it would lack the state of the rest.</returns>
    public Task<bool> ReplayAsync(string transcriptPath, int throughLineExclusive, CancellationToken ct) =>
        Task.Run(() => {
            var now    = TimeProvider.System.GetUtcNow();
            var number = 0;

            try {
                foreach (var line in File.ReadLinesShared(transcriptPath)) {
                    if (number >= throughLineExclusive) break;

                    ct.ThrowIfCancellationRequested();
                    _assembler.Replay(line, number++, now);
                }
            } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
                // The source has not written anything yet.
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception e) {
                FallBack();
                throw new CanonicalEncodingException(e);
            }

            return number >= throughLineExclusive;
        }, ct);

    /// <summary>The canonical form of a raw batch. Asking again for the same lines before <see cref="Acknowledged"/>,
    /// as after a failed send, returns the batch already built, because projecting is not repeatable.</summary>
    /// <exception cref="CanonicalEncodingException">The batch could not be projected. The route is inactive and the
    /// caller sends the batch raw.</exception>
    public CanonicalTranscriptBatch Encode(TranscriptBatch raw, DateTimeOffset receivedAt) {
        if (!Active) throw new InvalidOperationException("The route has fallen back to raw lines");

        if (_last is not null && raw.LineNumbers is { } numbers && numbers.AsSpan().SequenceEqual(_lastNumbers)) return _last;

        try {
            _last        = _assembler.Assemble(raw, receivedAt);
            _lastNumbers = raw.LineNumbers;

            return _last;
        } catch (Exception e) {
            FallBack();
            throw new CanonicalEncodingException(e);
        }
    }

    /// <summary>The server stored the last encoded batch.</summary>
    public void Acknowledged() {
        if (!Active) return;

        _assembler.Acknowledged();
        (_last, _lastNumbers) = (null, null);
    }

    public void FallBack() {
        Active = false;
        (_last, _lastNumbers) = (null, null);
    }
}
