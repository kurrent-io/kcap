namespace Capacitor.Cli.Core.Transcripts;

/// <summary>One transcript source's path to the canonical ingress. It owns the assembler whose projection state
/// follows the source's lines, so every batch of the source goes through one route, or the source falls back to raw
/// lines for good.</summary>
public sealed class CanonicalRoute {
    public const string HttpPath  = "/hooks/transcript/canonical";
    public const string HubMethod = "SendCanonicalBatch";

    readonly CanonicalBatchAssembler _assembler;
    readonly LineCaptureDecider?     _decide;

    int[]?                    _lastNumbers;
    CanonicalTranscriptBatch? _last;

    CanonicalRoute(CanonicalBatchAssembler assembler, LineCaptureDecider? decide) => (_assembler, _decide) = (assembler, decide);

    /// <summary>The route for a source, or null when its lines go raw: a vendor that is not canonical, or a server
    /// that cannot take canonical batches.</summary>
    /// <param name="decide">The redaction of the producer sending the source's lines, for a replay to decide a line
    /// <see cref="CaptureRule"/> cannot; without it such a line ends the replay.</param>
    public static CanonicalRoute? For(string? vendor, string sessionId, string? agentId, bool serverSupports, LineCaptureDecider? decide = null) {
        if (!serverSupports || !CanonicalVendors.Contains(vendor)) return null;

        var name = vendor ?? "claude";
        if (TranscriptProjection.For(name) is not { } projection || TranscriptSignals.For(name) is not { } signals) return null;

        // As the server files the stream: its batches carry these, and an attachment id hashes the session's.
        return new(new(projection, signals, SessionIds.Canonical(sessionId) ?? sessionId, SessionIds.Canonical(agentId) ?? agentId, name), decide);
    }

    public bool Active { get; private set; } = true;

    /// <summary>Projects the lines the server already holds, so the batches after them project as one uninterrupted
    /// run would. Lines are the agent's raw ones, read without denying it the file. A replay that fails leaves the
    /// projection half-advanced, so the route falls back.</summary>
    /// <returns>Whether every line before the bound was there to project as its capture had it. A file that is missing
    /// or shorter replays what it has, and a line whose capture stays undecided ends the replay there; either way the
    /// batches after it would lack the state of the rest.</returns>
    public Task<bool> ReplayAsync(string transcriptPath, int throughLineExclusive, CancellationToken ct) =>
        Task.Run(() => {
            var now    = TimeProvider.System.GetUtcNow();
            var number = 0;

            try {
                foreach (var line in File.ReadLinesShared(transcriptPath)) {
                    if (number >= throughLineExclusive) break;

                    ct.ThrowIfCancellationRequested();
                    if (_assembler.Replay(line, number++, now, _decide, ct)) continue;

                    // A line left undecided because the caller stopped the replay is that stop, as it is between lines.
                    ct.ThrowIfCancellationRequested();

                    return false;
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

    /// <summary>One line of <see cref="ReplayAsync"/>, for a caller that reads the transcript itself and feeds every line
    /// from the first, in order.</summary>
    /// <param name="ct">The replay's, which a redaction deciding the line stops at.</param>
    /// <returns>False for a line whose capture stays undecided: the replay cannot go past it.</returns>
    /// <exception cref="CanonicalEncodingException">The line could not be projected, and the route has fallen back.</exception>
    public bool Replay(string line, int lineNumber, DateTimeOffset receivedAt, CancellationToken ct = default) {
        try {
            return _assembler.Replay(line, lineNumber, receivedAt, _decide, ct);
        } catch (Exception e) {
            FallBack();
            throw new CanonicalEncodingException(e);
        }
    }

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
