using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Dsh;

namespace Capacitor.Cli.Harness.Dsh;

/// <summary>
/// Imports DeepSeek Harness sessions from the JSONL copies the kcap plugin writes, the same files
/// the live watcher tails. Sessions dsh ran before the plugin was installed have no copy and are not
/// found: dsh's own logs are zstd-compressed. Completeness is the server's transcript watermark.
/// </summary>
/// <param name="detectRepository">The repository a session's cwd belongs to, without PR detection.</param>
internal sealed class DshImportSource(
        string sessionsDir, Func<string, Task<RepositoryPayload?>> detectRepository, TimeProvider time) : IImportSource {
    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static string NormalizeForComparison(string path) {
        try {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            return path.TrimEnd('/', '\\');
        }
    }

    public HarnessId Vendor => HarnessId.Dsh;

    public bool IsAvailable => Directory.Exists(sessionsDir);

    /// <summary>A dsh subagent is its own session, adopted under its parent by session-start.</summary>
    public bool AttachesChildContentOnReplay => false;

    /// <summary>dsh's own titles are not carried to the server, so the title pipeline names them.</summary>
    public bool SupportsTitleGeneration => true;

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();

        if (!Directory.Exists(sessionsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        foreach (var jsonl in GuardedDiscovery.EnumerateFiles(sessionsDir, "*.jsonl", recursive: false)) {
            ct.ThrowIfCancellationRequested();

            var rawId = Path.GetFileNameWithoutExtension(jsonl);
            if (string.IsNullOrEmpty(rawId)) continue;
            var sessionId = DshSessionId.Canonicalize(rawId);

            if (sessionFilter is not null
             && !string.Equals(sessionId, sessionFilter, StringComparison.Ordinal)
             && !string.Equals(rawId, sessionFilter, StringComparison.Ordinal))
                continue;

            var header = DshSessionHeader.TryRead(jsonl);

            if (normalizedCwd is not null
             && (header?.Cwd is null || !NormalizeForComparison(header.Cwd).Equals(normalizedCwd, PathComparison)))
                continue;

            var firstTimestamp = header?.CreatedAt;
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(jsonl); } catch { /* best effort */ }
            }

            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      sessionId,
                Vendor:         Vendor,
                Cwd:            header?.Cwd,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"] = jsonl,
                    ["Cwd"]            = header?.Cwd,
                    ["ParentSession"]  = header?.ParentSession,
                }));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
            };

            int? lastNonBlankIndex;
            int? lastRelevantIndex;
            int  nonBlankCount;
            try {
                (lastNonBlankIndex, lastRelevantIndex, nonBlankCount) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "transcript read failed"));
                continue;
            }

            if (lastNonBlankIndex is null) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "empty transcript"));
                continue;
            }

            if (nonBlankCount < ctx.MinLines) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.TooShort, totalLines: nonBlankCount));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
            } catch (SessionDeletedException) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.Deleted, totalLines: nonBlankCount));
                continue;
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: nonBlankCount,
                                               probeErrorReason: "watermark probe failed"));
                continue;
            }

            meta.LastTimestamp ??= TryGetLastWriteUtc(transcriptPath);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            var lastImportable = lastRelevantIndex ?? lastNonBlankIndex.Value;

            if (serverLastLine is { } srv) {
                if (srv >= lastImportable) {
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                } else {
                    status       = ImportCommand.ClassificationStatus.Partial;
                    resumeFromLn = srv + 1;
                }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId      = s.SessionId,
                FilePath       = "",
                EncodedCwd     = "",
                Meta           = meta,
                Status         = status,
                Vendor         = Vendor,
                ResumeFromLine = resumeFromLn,
                TotalLines     = nonBlankCount,
                SourceMeta     = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var cwd = classification.SourceMeta!.TryGetValue("Cwd", out var c) ? c as string : null;

        var startPayload = BuildSessionStartPayload(classification.SessionId, cwd, classification.Meta.FirstTimestamp);

        if (classification.SourceMeta!.TryGetValue("ParentSession", out var ps) && ps is string parentRaw
         && !string.IsNullOrWhiteSpace(parentRaw)) {
            startPayload["parent_session_id"] = DshSessionId.Canonicalize(parentRaw);
        }

        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        if (cwd is { Length: > 0 }) {
            try {
                if (await detectRepository(cwd) is { } repo) startPayload["repository"] = RepositoryDetection.BuildRepositoryNode(repo);
            } catch {
                // Repository attribution is best effort.
            }
        }

        if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/dsh", startPayload, ct))
            return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   transcriptPath,
                agentId:    null,
                startLine:  startLine,
                time:       time,
                vendor:     Vendor,
                progress:   ctx.Progress);
        } catch {
            return ImportOutcome.Failed;
        }

        if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/dsh",
                BuildSessionEndPayload(classification.SessionId, cwd, classification.Meta.LastTimestamp), ct))
            return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static JsonObject BuildSessionStartPayload(string sessionId, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "historical-import",
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (endedAt is { } ts) payload["ended_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    async Task<bool> PostSyntheticHookAsync(
        HttpClient client, string baseUrl, string routeSegment, JsonObject payload, CancellationToken ct
    ) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{routeSegment}", content, time, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    static DateTimeOffset? TryGetLastWriteUtc(string path) {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    static ImportCommand.SessionClassification MakeClassification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = HarnessId.Dsh,
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    static async Task<(int? LastNonBlankIndex, int? LastRelevantIndex, int NonBlankCount)> ReadTranscriptStatsAsync(
        string transcriptPath, CancellationToken ct
    ) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx         = null;
        int? lastRelevantIdx = null;
        var  count           = 0;
        var  lineIdx         = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;

                if (IsImportRelevantLine(line)) lastRelevantIdx = lineIdx;
            }
            lineIdx++;
        }
        return (lastIdx, lastRelevantIdx, count);
    }

    /// <summary>
    /// The lines the server's dsh normalizer turns into canonical events: a human prompt, an
    /// assistant message, a tool result that is not a compaction rewrite, a PTC sub-call. Only those
    /// advance the transcript watermark, so this must match the normalizer, or a session ending in a
    /// skipped line re-imports forever.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            return root.Str("type") switch {
                "user/message"                       => root.Obj("data")?.Obj("source")?.Str("kind") == "user",
                "tool/result"                        => root.Obj("surfaceOp") is null,
                "assistant/message" or "tool/ptc-dispatch" => true,
                _                                    => false,
            };
        } catch {
            return false;
        }
    }

    async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct) {
        using var resp = await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", time, ct: ct);

        SessionDeletedException.ThrowIfDeleted(resp, sessionId);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");

        var       body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);

        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
    }
}
