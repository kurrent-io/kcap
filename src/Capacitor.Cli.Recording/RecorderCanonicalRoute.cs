using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Whether a recorder's lines go out as canonical batches or raw. A canonical batch goes through a route that has
/// projected every line before the cursor, raw as the agent wrote them, so it carries what one uninterrupted run would.
/// Each connection asks its server, and a source that went raw on one gets a fresh route on the next.
/// </summary>
/// <remarks>Called from the recorder's first connect and from its drains and re-registrations, which take turns. Only
/// the first connect can overlap a re-registration, so what a connect opened is installed only on that connection.</remarks>
/// <param name="decide">The recorder's own capture, which a replay decides a large line with.</param>
internal sealed class RecorderCanonicalRoute(IRecorderTransport transport, RecordingSource source, TimeProvider time, Action<string> log, LineCaptureDecider decide) {
    readonly Lock _gate = new();

    int             _connection;
    bool?           _supported;
    CanonicalRoute? _route;
    bool            _raw;
    int?            _failedAt;

    /// <summary>A new connection: its server answers for itself, and a source that went raw gets another chance.</summary>
    public void Reset() {
        lock (_gate) {
            _connection++;
            (_supported, _route, _raw, _failedAt) = (null, null, false, null);
        }
    }

    /// <summary>Readies a route for the lines from <paramref name="cursor"/> on, when they are to go canonical and none is
    /// ready. One past line zero must first see every line before the cursor, so the result is then a consumer for
    /// <see cref="TranscriptReplay"/>, given what the replay runs under, a stop or its own time, which a large line's
    /// redaction ends with; and what to call after the replay. Null otherwise.</summary>
    public async Task<(Func<CancellationToken, TranscriptReplayConsumer> Consumer, Action Replayed)?> OpenAsync(int cursor, CancellationToken ct) {
        if (!CanonicalVendors.Contains(source.Vendor)) return null;

        bool? supported;
        int   connection;

        lock (_gate) {
            if (_raw || _route is not null) return null;

            (supported, connection) = (_supported, _connection);
        }

        if (supported is null) {
            try {
                supported = await transport.SupportsCanonicalAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Stopping: whatever sends next asks again.
                return null;
            }

            lock (_gate) {
                if (connection != _connection) return null;

                _supported = supported;
            }
        }

        if (CanonicalRoute.For(source.Vendor, source.SessionId, source.AgentId, supported.Value, decide) is not { } route) return null;

        if (cursor == 0) {
            Install(route, connection, missing: null);

            return null;
        }

        var     seen    = 0;
        string? missing = null;
        var     replay  = CancellationToken.None;

        return (token => {
            replay = token;

            return new(WindowOnly: false, line => {
                try {
                    if (route.Replay(line, seen, time.GetUtcNow(), replay)) {
                        seen++;

                        return false;
                    }

                    missing = $"Redacting line {seen} to tell whether its capture kept it ran out of time";
                } catch (CanonicalEncodingException ex) {
                    missing = $"Line {seen} could not be projected: {ex.InnerException?.Message}";
                }

                return true;
            });
        }, () => {
            // A stop says nothing of the lines, and the next send opens a route again.
            if (ct.IsCancellationRequested) return;

            Install(route, connection, missing ?? (seen == cursor ? null
                : replay.IsCancellationRequested ? $"Projecting the lines before line {cursor} ran out of time at line {seen}"
                : $"The transcript has {seen} of the {cursor} lines before the cursor"));
        });
    }

    /// <summary>Sends <paramref name="chunk"/> as a canonical batch when a route is ready, or returns false for the caller to
    /// send it raw. A batch that cannot be projected, or one refused as unsupported, keeps the source raw until the next
    /// connect. A failed send throws and drops the route, which projected lines the server may not have; a second failure
    /// before <paramref name="cursor"/> moves keeps the source raw too.</summary>
    public async Task<bool> TrySendAsync(TranscriptBatch chunk, int cursor, CancellationToken ct) {
        CanonicalRoute? route;

        lock (_gate) route = _route;

        if (route is not { Active: true } || chunk.Lines.Length == 0) return false;

        CanonicalTranscriptBatch batch;

        try {
            batch = route.Encode(chunk, time.GetUtcNow());
        } catch (CanonicalEncodingException ex) {
            GoRaw(route, $"Lines {chunk.LineNumbers![0]}-{chunk.LineNumbers[^1]} could not be projected: {ex.InnerException?.Message}");

            return false;
        }

        CanonicalSendResult sent;

        try {
            sent = await transport.SendCanonicalBatchAsync(batch, ct);
        } catch (Exception) {
            Failed(route, cursor);

            throw;
        }

        if (sent.Unsupported) {
            route.FallBack();
            GoRaw(route, "The server refused a canonical batch as unsupported");

            return false;
        }

        route.Acknowledged();

        return true;
    }

    /// <param name="missing">Why the route lacks a line before the cursor, which keeps the source raw; null installs it.</param>
    void Install(CanonicalRoute route, int connection, string? missing) {
        lock (_gate) {
            if (connection != _connection) return;

            if (missing is null) {
                _route = route;

                return;
            }

            _raw = true;
        }

        log($"{missing}; sending raw lines until the next connect");
    }

    void GoRaw(CanonicalRoute route, string why) {
        lock (_gate) {
            if (_route != route) return;

            (_route, _raw) = (null, true);
        }

        log($"{why}; sending raw lines until the next connect");
    }

    void Failed(CanonicalRoute route, int cursor) {
        lock (_gate) {
            if (_route != route) return;

            _route = null;

            if (_failedAt != cursor) {
                _failedAt = cursor;

                return;
            }

            _raw = true;
        }

        log($"Canonical batches from line {cursor} failed twice; sending raw lines until the next connect");
    }
}
