using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Classification for a routed source whose discovered sessions each carry one JSONL transcript under
/// <c>SourceMeta["TranscriptPath"]</c>. Every classification has an empty <c>FilePath</c>, which keeps it on the
/// routed import phase.
/// </summary>
internal static class RoutedImportClassifier {
    public static async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            RoutedClassifyOptions            options,
            CancellationToken                ct
        ) {
        var client  = new RoutedImportClient(ctx.HttpClient, options.Time, ctx.BaseUrl);
        var vendor  = options.Vendor;
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta["TranscriptPath"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
                LastTimestamp  = options.RecordedEndedAt?.Invoke(s),
            };

            TranscriptStats stats;
            try {
                stats = await TranscriptStats.ReadAsync(transcriptPath, options.IsImportRelevantLine, ct);
            } catch {
                results.Add(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
                continue;
            }

            if (stats.LastNonBlankIndex is not { } lastNonBlank) {
                results.Add(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript"));
                continue;
            }

            var nonBlankCount = stats.NonBlankCount;

            if (nonBlankCount < ctx.MinLines) {
                results.Add(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.TooShort, nonBlankCount));
                continue;
            }

            if (ctx.ExcludeBeforeProbeAsync is { } exclude
             && await exclude(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.New, nonBlankCount)) is { } excluded) {
                results.Add(excluded);
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await client.FetchServerLastLineAsync(s.SessionId, ct);
            } catch (OperationCanceledException) when (options.RethrowProbeCancellation) {
                throw;
            } catch (SessionDeletedException) {
                results.Add(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.Deleted, nonBlankCount));
                continue;
            } catch {
                results.Add(Classification(s, meta, vendor, ImportCommand.ClassificationStatus.ProbeError, nonBlankCount, "watermark probe failed"));
                continue;
            }

            meta.LastTimestamp ??= options.EndedAt(transcriptPath);

            // A transcript can end on lines the normalizer skips; they never advance the watermark, so comparing
            // against the raw tail would leave a complete session Partial forever.
            var lastImportable = stats.LastRelevantIndex ?? lastNonBlank;

            var resolution = serverLastLine is { } srv
                ? (options.ResolveWatermark ?? Compare)(s, srv, lastImportable)
                : new WatermarkResolution(ImportCommand.ClassificationStatus.New, 0, s.SourceMeta);

            results.Add(new ImportCommand.SessionClassification {
                SessionId      = s.SessionId,
                FilePath       = "",
                EncodedCwd     = "",
                Meta           = meta,
                Status         = resolution.Status,
                Vendor         = vendor,
                ResumeFromLine = resolution.ResumeFromLine,
                TotalLines     = nonBlankCount,
                SourceMeta     = resolution.SourceMeta,
            });
        }

        return results;
    }

    /// <summary>A routed classification that resumes nowhere: a local failure, an exclusion candidate, or a server-side verdict.</summary>
    public static ImportCommand.SessionClassification Classification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        HarnessId                          vendor,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = vendor,
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    static WatermarkResolution Compare(DiscoveredSession s, int serverLastLine, int lastImportable) =>
        serverLastLine >= lastImportable
            ? new(ImportCommand.ClassificationStatus.AlreadyLoaded, 0, s.SourceMeta)
            : new(ImportCommand.ClassificationStatus.Partial, serverLastLine + 1, s.SourceMeta);
}
