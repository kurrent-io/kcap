using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Titles;
using Capacitor.Cli.Recording.Harness.Kiro;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Harness.Kiro;

/// <summary>
/// Discover + classify + import historical AWS Kiro CLI sessions from the
/// append-only JSONL logs under <c>~/.kiro/sessions/cli/{id}.jsonl</c> (each the
/// same lines the live watcher tails, so live and historical ingest converge on
/// the server's <c>KiroTranscriptNormalizer</c>). The sibling <c>{id}.json</c>
/// carries cwd / model / title / timestamps. Every JSONL line maps to a canonical
/// event (<c>Prompt</c> / <c>AssistantMessage</c> / <c>ToolResults</c>), so the
/// "last import-relevant line" is simply the last non-blank line.
/// </summary>
internal sealed class KiroImportSource : IImportSource {
    readonly string                                 _sessionsDir;
    readonly KiroCrewPaths                          _crew;
    readonly TimeProvider                           _time;

    IReadOnlyDictionary<Guid, string>?              _crewParents;
    ILookup<Guid, Guid>?                            _crewChildren;

    public KiroImportSource(
        ConfigRoot                              config,
        string                                  sessionsDir,
        KiroCrewPaths                           crew,
        GitProviderRouter                        router,
        TimeProvider                            time
    ) {
        _sessionsDir  = sessionsDir;
        _crew         = crew;
        _time         = time;
    }

    public HarnessId Vendor => HarnessId.Kiro;

    public bool IsAvailable => Directory.Exists(_sessionsDir);

    /// <summary>
    /// False — Kiro names each session in the sibling <c>{id}.json</c>, which
    /// ImportSessionAsync forwards via <c>/hooks/set-title</c>, so the LLM title
    /// pipeline would only burn tokens to overwrite a good title.
    /// </summary>
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => false; // no child import

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? ImportPaths.NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();

        if (!Directory.Exists(_sessionsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        foreach (var jsonl in GuardedDiscovery.EnumerateFiles(_sessionsDir, "*.jsonl", recursive: false)) {
            ct.ThrowIfCancellationRequested();

            // Filename stem is the dashed session UUID Kiro uses for both files.
            var dashed = Path.GetFileNameWithoutExtension(jsonl);

            // Kiro session files are UUID-named, and the live hook path rejects
            // non-GUID ids. Apply the same guard so a stray *.jsonl (backup,
            // export, debug dump, partial rename) isn't discovered as a session
            // and then probed against /api/sessions/{id}/... with a non-session id.
            if (!Guid.TryParse(dashed, out _)) continue;

            var dashless = dashed.Replace("-", "");

            if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                continue;

            // The metadata sibling sits next to the .jsonl (respects a custom
            // sessions dir), so derive it from the discovered path rather than
            // the global ~/.kiro location.
            var meta = KiroSessionMeta.TryRead(Path.ChangeExtension(jsonl, ".json"));

            if (normalizedCwd is not null
             && (meta?.Cwd is null || !ImportPaths.NormalizeForComparison(meta.Cwd).Equals(normalizedCwd, ImportPaths.PathComparison)))
                continue;

            // Session-start proxy: the .json created_at, else the transcript's
            // filesystem birth time (Linux ext4 reports mtime — best effort).
            var firstTimestamp = meta?.CreatedAt;
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(jsonl); } catch { /* best effort */ }
            }

            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      dashless,
                Vendor:         Vendor,
                Cwd:            meta?.Cwd,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"]    = jsonl,
                    ["DashedSessionId"]   = dashed,
                    ["Cwd"]               = meta?.Cwd,
                    ["Title"]             = meta?.Title,
                    ["Model"]             = meta?.Model,
                    ["LastTimestamp"]     = meta?.UpdatedAt,
                }));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) =>
        RoutedImportClassifier.ClassifyAsync(sessions, ctx, new RoutedClassifyOptions(Vendor, _time, IsImportRelevantLine, ImportPaths.TryGetLastWriteUtc) {
            RecordedEndedAt = s => s.SourceMeta.TryGetValue("LastTimestamp", out var lt) ? lt as DateTimeOffset? : null,
        }, ct);

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var cwd    = classification.SourceMeta!.TryGetValue("Cwd", out var c) ? c as string : null;
        var dashed = classification.SourceMeta!.TryGetValue("DashedSessionId", out var d) ? d as string : null;
        var model  = classification.SourceMeta!.TryGetValue("Model", out var m) ? m as string : null;

        // Lifecycle uses the dashed id (matches the live agentSpawn hook so a
        // re-import of a live session dedupes); the transcript route uses the
        // dashless id (the canonical stream key). Lifecycle-before-transcript:
        // a transcript that advances the watermark past a failed lifecycle POST
        // would leave the session permanently lifecycle-less.
        var lifecycleId = dashed ?? classification.SessionId;

        var client       = new RoutedImportClient(ctx.HttpClient, _time, ctx.BaseUrl);
        var startPayload = SyntheticHookPayloads.SessionStart("agentSpawn", lifecycleId, cwd, classification.Meta.FirstTimestamp, source: null, model: model);
        // Sessions import in parallel, so each side of a sub-agent link names the other.
        _crewParents  ??= KiroCrewParentResolver.AllParents(_crew, _sessionsDir);
        _crewChildren ??= _crewParents.ToLookup(kv => Guid.Parse(kv.Value), kv => kv.Key);

        // The server takes a bounded batch per session-start, so children beyond it follow in repeat
        // session-starts, which its deterministic ids make idempotent.
        var childBatches = new List<string[]>();

        if (Guid.TryParse(lifecycleId, out var self)) {
            if (_crewParents.TryGetValue(self, out var parent)) startPayload["parent_session_id"] = parent;
            // Sessions import in parallel, so each side of a continuation names the other too.
            if (KiroCrewParentResolver.PreviousOf(_crew, lifecycleId) is { } previous) startPayload["previous_session_id"] = previous;
            if (KiroCrewParentResolver.NextOf(_crew, lifecycleId) is { } next) startPayload["next_session_id"] = next;

            childBatches = _crewChildren[self].Select(c => c.ToString("D")).Chunk(KiroCrewParentResolver.MaxChildrenPerStart).ToList();
            if (childBatches.Count > 0) startPayload["subagent_session_ids"] = KiroCrewParentResolver.SessionIdArray(childBatches[0]);
        }
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await client.PostSyntheticHookAsync("session-start/kiro", startPayload, ct);
        if (!startOk) return ImportOutcome.Failed;

        foreach (var batch in childBatches.Skip(1)) {
            var repeat = startPayload.DeepClone().AsObject();
            repeat["subagent_session_ids"] = KiroCrewParentResolver.SessionIdArray(batch);

            if (!await client.PostSyntheticHookAsync("session-start/kiro", repeat, ct))
                return ImportOutcome.Failed;
        }

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        // Enrich the turn-final assistant lines with per-turn usage (credits /
        // context%) read from the sibling {id}.json — Kiro's JSONL carries none.
        // When there's usage, send an enriched copy (same lines/order/numbers, a
        // _kcap_usage field added to the anchor lines); otherwise send the file
        // as-is. The enriched copy is a temp file SendTranscriptBatches reads.
        var anchors  = KiroUsage.AnchorMap(SafeReadText(Path.ChangeExtension(transcriptPath, ".json")));
        var sendPath = transcriptPath;
        string? enrichedTemp = null;

        if (anchors.Count > 0) {
            enrichedTemp = Path.Combine(Path.GetTempPath(), $"kcap-kiro-usage-{classification.SessionId}-{Guid.NewGuid():N}.jsonl");
            var enriched = File.ReadLinesShared(transcriptPath)
                .Select(l => string.IsNullOrWhiteSpace(l) ? l : KiroUsage.EnrichLine(l, anchors));
            await File.WriteAllLinesAsync(enrichedTemp, enriched, ct);
            sendPath = enrichedTemp;
        }

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   sendPath,
                agentId:    null,
                startLine:  startLine,
                time:       _time,
                vendor:     Vendor,
                progress:   ctx.Progress);
        } catch {
            return ImportOutcome.Failed;
        } finally {
            try { if (enrichedTemp is not null && File.Exists(enrichedTemp)) File.Delete(enrichedTemp); }
            catch { /* best effort */ }
        }

        // Forward Kiro's own session title, waiting out the server's projection lag
        // (best-effort — a title miss must not fail the import).
        if (classification.SourceMeta!.TryGetValue("Title", out var titleObj)
         && titleObj is string title
         && !string.IsNullOrWhiteSpace(title)) {
            await ImportHarnessTitle.PostAsync(
                ctx.HttpClient, _time, ctx.BaseUrl, classification.SessionId,
                new HarnessTitlePost(title, HarnessTitleKind.Auto, null),
                ctx.Progress, ct);
        }

        var endOk = await client.PostSyntheticHookAsync(
            "session-end/kiro",
            SyntheticHookPayloads.SessionEnd("sessionEnd", lifecycleId, "historical-import", cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    internal static string SafeReadText(string path) {
        try { return File.Exists(path) ? File.ReadAllTextShared(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// True when the line maps to a canonical event under the server's
    /// KiroTranscriptNormalizer — kind <c>Prompt</c> / <c>AssistantMessage</c> /
    /// <c>ToolResults</c>. Other kinds are skipped server-side and never advance
    /// the transcript watermark, so a fully-imported session stays AlreadyLoaded.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Str("kind") is "Prompt" or "AssistantMessage" or "ToolResults";
        } catch {
            return false;
        }
    }
}
