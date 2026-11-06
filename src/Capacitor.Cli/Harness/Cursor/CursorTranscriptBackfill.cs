using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Harness.Cursor;

// Backfill respects the same source watermark, quarantine and attachment barriers as live capture.
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BatchPostTimeout = TimeSpan.FromMilliseconds(1500);

    /// <param name="FirstUnsentLine">The first line read and not posted: one redaction held, an unterminated record
    /// that does not parse, or the first the budget or a failed post left behind. None for a quarantined
    /// session, which is dropped on purpose.</param>
    /// <param name="TailUnread">The drain stopped before it could read what was left to send.</param>
    public readonly record struct Stats(int LinesPosted, bool Failed, int? FirstUnsentLine = null, bool TailUnread = false) {
        public bool TailUnsent => FirstUnsentLine is not null || TailUnread;
    }

    public static Task<Stats> RunAsync(
            CursorMarkers     markers,
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            TimeProvider      time,
            CancellationToken ct,
            string?           agentId    = null,
            bool              finalDrain = false
        ) => RunAsync(markers, client, baseUrl, sessionId, transcriptPath, budget, time, ct,
            SecretRedactor.WatcherPatterns, agentId, finalDrain);

    internal static async Task<Stats> RunAsync(
            CursorMarkers     markers,
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            TimeProvider      time,
            CancellationToken ct,
            SecretPatterns    patterns,
            string?           agentId    = null,
            bool              finalDrain = false
        ) {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) {
            return new Stats(0, false);
        }

        if (markers.IsQuarantined(sessionId)) {
            return new Stats(0, false);
        }

        if (markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false, TailUnread: true);
        }

        int resumeFrom;

        var watermarkUrl = string.IsNullOrEmpty(agentId)
            ? $"{baseUrl}/api/sessions/{sessionId}/last-line"
            : $"{baseUrl}/api/sessions/{sessionId}/last-line?agentId={Uri.EscapeDataString(agentId)}";

        try {
            using var resp = await client.GetOnceAsync(
                watermarkUrl,
                time,
                WatermarkTimeout,
                ct
            );

            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) {
                resumeFrom = 0;
            } else if (!resp.IsSuccessStatusCode) {
                return new Stats(0, Failed: true, TailUnread: true);
            } else {
                var       body = await resp.Content.ReadAsStringAsync(ct);
                using var doc  = JsonDocument.Parse(body);

                resumeFrom = doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                    ? ln.GetInt32() + 1
                    : 0;
            }
        } catch { return new Stats(0, Failed: true, TailUnread: true); }

        List<string> lines;
        List<int>    lineNumbers;
        List<int>    readNumbers;
        int?         heldRecord;

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var policy = finalDrain
                ? WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete
                : WatchCommand.IncompleteFinalLinePolicy.Hold;
            var read = await WatchCommand.ReadNewCompleteLinesAsync(stream, resumeFrom, policy, ct);

            var captured = TranscriptCapture.EncodeUntilHeld(read.Lines, budget, time,
                (reason, count) => Console.Error.WriteLine($"Cursor capture loss: {count} record(s), {CaptureLossMarker.ReasonName(reason)}"), patterns);
            lines       = captured.Lines;
            lineNumbers = read.LineNumbers.GetRange(0, captured.Consumed);
            readNumbers = read.LineNumbers;
            heldRecord  = read.HeldIncompleteFinalLine ? read.NextPosition : null;
        } catch { return new(0, Failed: true, TailUnread: true); }

        int? FirstUnsent(int posted) => posted < readNumbers.Count ? readNumbers[posted] : heldRecord;

        if (lines.Count == 0 || budget()) return new(0, Failed: false, FirstUnsent(0));

        if (markers.IsQuarantined(sessionId)) return new Stats(0, false);

        if (markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false, FirstUnsent(0));
        }

        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [..lines],
            LineNumbers = [..lineNumbers],
            Vendor      = "cursor",
        };

        var posted = 0;
        try {
            foreach (var chunk in TranscriptBatchBuffer.Split(batch)) {
                if (markers.IsQuarantined(sessionId)) return new Stats(posted, Failed: false);
                if ((!finalDrain || posted == 0) && budget()
                    || markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound))
                    return new Stats(posted, Failed: false, FirstUnsent(posted));
                var json = JsonSerializer.Serialize(chunk, CapacitorJsonContext.Default.TranscriptBatch);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostOnceAsync($"{baseUrl}/hooks/transcript", content, time, BatchPostTimeout, ct);
                if (!response.IsSuccessStatusCode) return new Stats(posted, Failed: true, FirstUnsent(posted));
                posted += chunk.Lines.Length;
            }
            return new Stats(posted, Failed: false, FirstUnsent(posted));
        } catch {
            return new Stats(posted, Failed: true, FirstUnsent(posted));
        }
    }
}
