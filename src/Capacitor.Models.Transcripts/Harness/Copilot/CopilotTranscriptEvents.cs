using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// <summary>
/// Projects GitHub Copilot's <c>events.jsonl</c> (Copilot CLI, VS Code's Copilot harness, and the lines kcap writes for
/// VS Code's Local harness) into canonical events. Every line is an envelope
/// <c>{"type","data","id","timestamp","parentId","agentId"?}</c>:
/// <list type="bullet">
///   <item><c>session.start</c>: a SessionStarted carrying the transcript's git context.</item>
///   <item><c>session.info</c> saying this session was forked: a SessionStarted naming the parent.</item>
///   <item><c>session.binary_asset</c>: nothing; its bytes wait in the context for the prompt that names them.</item>
///   <item><c>user.message</c>: the prompt as typed, without Copilot's injected wrapper, with its attachments.</item>
///   <item><c>assistant.message</c>: thinking, text, then one event per tool request; the first carries the output.</item>
///   <item><c>tool.execution_complete</c>: a result with Copilot's outcome flag.</item>
///   <item><c>session.usage_record</c>, <c>session.shutdown</c>, <c>session.usage_checkpoint</c> and
///   <c>kcap.vscode_request_credits</c>: tokens and billing.</item>
/// </list>
/// A subagent's lines sit in its parent's transcript, tagged with an envelope <c>agentId</c>. A fork's transcript
/// repeats its parent's lines after its own start, and those project to nothing. Copilot's vocabulary is undocumented
/// and changes often, so any other type is noise.
/// </summary>
public sealed partial class CopilotTranscriptEvents : ITranscriptProjection {
    public static readonly CopilotTranscriptEvents Instance = new();

    /// Metadata key, value <c>true</c>, on the SessionStarted that links a fork to its parent; a caller that stores
    /// it reports it back through <see cref="CopilotContext.Seed"/>.
    public const string ForkMarkerKey = "$copilotForkMarker";

    static readonly FrozenDictionary<string, string> ForkMarker =
        new Dictionary<string, string> { [ForkMarkerKey] = "true" }.ToFrozenDictionary(StringComparer.Ordinal);

    CopilotTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new CopilotContext(Canonical(sessionId));

    /// The receive time goes unused: a line with no time of its own takes the earliest instant, as the server
    /// stores it.
    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (CopilotContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") is not { } type || root.Obj("data") is not { } data) return ProjectionResult.Empty;

            var recordTs = root.Str("timestamp");
            var at       = ParseTime(recordTs);

            if (IsCopiedFromParent(type, data, at, ctx)) return ProjectionResult.Empty;

            var e = new Envelope(TranscriptIds.CopilotRecord(root.Str("id"), line), at, recordTs, root.Str("agentId"));

            if (type is "session.start" or "session.resume") {
                ctx.RunMarker               = e.Id;
                ctx.RunHasUsageRecords      = false;
                ctx.RunMessagesReportOutput = null;
            }

            return type switch {
                "session.start"               => SessionStart(data, e),
                "session.info"                => ForkLink(data, e, ctx),
                "session.binary_asset"        => BinaryAsset(data, ctx),
                "user.message"                => UserMessage(data, e, ctx),
                "assistant.message"           => AssistantMessage(data, e, ctx),
                "tool.execution_complete"     => ToolResult(data, e),
                "session.usage_record"        => UsageRecord(data, e, ctx),
                "session.usage_checkpoint"    => Metrics(data, ctx) is { } metrics ? One(metrics, e.Id, e) : ProjectionResult.Empty,
                "kcap.vscode_request_credits" => RequestCredits(data, e),
                "session.shutdown"            => Shutdown(data, e, ctx),
                _                             => ProjectionResult.Empty,
            };
        }
    }

    /// A fork's copied line, which keeps only the parent's last totals: the fork's own cumulative totals include them.
    /// A usage record after those totals makes them stale.
    static bool IsCopiedFromParent(string type, JsonElement data, DateTimeOffset at, CopilotContext ctx) {
        if (type == "session.start") {
            var start = ParseTime(data.Str("startTime"));
            ctx.CopiedLinesBefore        = start == DateTimeOffset.MinValue ? null : start;
            ctx.StartUnknown             = false;
            ctx.IsFork                   = false;
            ctx.InheritedNanoAiu         = null;
            ctx.InheritedPremiumRequests = null;

            return false;
        }

        if (ctx.StartUnknown) return true;

        if (ctx.CopiedLinesBefore is not { } before || at == DateTimeOffset.MinValue) return false;

        if (at >= before) {
            ctx.CopiedLinesBefore = null;

            return false;
        }

        if (type is "session.usage_checkpoint" or "session.shutdown") {
            ctx.InheritedNanoAiu         = data.Num("totalNanoAiu");
            ctx.InheritedPremiumRequests = Dbl(data, "totalPremiumRequests");
        } else if (type == "session.usage_record") {
            ctx.InheritedNanoAiu         = null;
            ctx.InheritedPremiumRequests = null;
        }

        return true;
    }

    static ProjectionResult SessionStart(JsonElement data, Envelope e) {
        var context = data.Obj("context");
        var started = new SessionStarted { Timestamp = Timestamp.FromDateTimeOffset(e.At) };
        started.Extensions[CopilotExtension.Slug] = CopilotExtension.Session(
            cwd:            context?.Str("cwd"),
            gitRoot:        context?.Str("gitRoot"),
            branch:         context?.Str("branch"),
            repository:     context?.Str("repository"),
            repositoryHost: context?.Str("repositoryHost"),
            hostType:       context?.Str("hostType"),
            copilotVersion: data.Str("copilotVersion"),
            producer:       data.Str("producer"));

        return One(started, e.Id, e);
    }

    /// Either session reads <c>"name" (id)</c>, or the bare id when it has none; a fork cut short names the event.
    [GeneratedRegex("""^Forked from (?:".*" \()?(?<parent>[0-9a-fA-F-]{36})\)?(?: before event \S+)? as (?:".*" \()?(?<fork>[0-9a-fA-F-]{36})\)?\.$""", RegexOptions.CultureInvariant)]
    private static partial Regex ForkedFrom();

    /// Only the fork's own marker links: a parent's says it was forked into another session, and a fork of a fork
    /// repeats its parent's among the copied lines. There is no link back on the parent, which can be forked many times.
    static ProjectionResult ForkLink(JsonElement data, Envelope e, CopilotContext ctx) {
        if (data.Str("infoType") != "fork" || data.Str("message") is not { } message) return ProjectionResult.Empty;
        if (ForkedFrom().Match(message) is not { Success: true } match) return ProjectionResult.Empty;

        var parent = Canonical(match.Groups["parent"].Value);
        var fork   = Canonical(match.Groups["fork"].Value);
        if (fork != ctx.SessionId || parent == fork) return ProjectionResult.Empty;

        var started = new SessionStarted { Timestamp = Timestamp.FromDateTimeOffset(e.At), PreviousSessionId = parent };
        started.Extensions[CopilotExtension.Slug] = CopilotExtension.Session(source: "fork");
        ctx.IsFork = true;

        return ProjectionResult.Of([Event(started, e.Id, e.At, e.RecordTs) with { VendorMetadata = ForkMarker }]);
    }

    static ProjectionResult BinaryAsset(JsonElement data, CopilotContext ctx) {
        if (data.Str("assetId") is not { Length: > 0 } assetId
         || data.Str("mimeType") is not { Length: > 0 } mimeType
         || data.Str("data") is not { Length: > 0 } base64) return ProjectionResult.Empty;

        // Bounds the decode itself: the stash evicts only after a payload is decoded.
        if ((long)base64.Length / 4 * 3 > CopilotAssetStash.MaxBytes) return ProjectionResult.Empty;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch (FormatException) { return ProjectionResult.Empty; }

        ctx.Assets.Put(assetId, mimeType, bytes);

        return ProjectionResult.Empty;
    }

    /// <c>content</c> is the prompt as typed; <c>transformedContent</c> wraps it in Copilot's injected reminders. A
    /// prompt with only attachments is still a turn.
    static ProjectionResult UserMessage(JsonElement data, Envelope e, CopilotContext ctx) {
        var text        = data.Str("content");
        var attachments = Attachments(data, e.Id, ctx);
        if (string.IsNullOrEmpty(text) && attachments is null) return ProjectionResult.Empty;

        var message = new UserMessageReceived { Content = text ?? "", Timestamp = Timestamp.FromDateTimeOffset(e.At) };
        TagAgent(message.Extensions, e.AgentId);

        return ProjectionResult.Of([Event(message, e.Id, e.At, e.RecordTs) with { Attachments = attachments }]);
    }

    /// An attachment whose bytes never arrived, or were evicted, is left off.
    static List<TranscriptAttachment>? Attachments(JsonElement data, Guid messageId, CopilotContext ctx) {
        if (data.Arr("attachments") is not { } attachments) return null;

        List<TranscriptAttachment>? resolved = null;

        foreach (var attachment in attachments.EnumerateArray()) {
            if (attachment.Str("assetId") is not { Length: > 0 } assetId || ctx.Assets.Get(assetId) is not { } asset) continue;

            var fileName    = attachment.Str("displayName") is { Length: > 0 } name ? name : assetId;
            var contentType = attachment.Str("mimeType") is { Length: > 0 } mime ? mime : asset.MimeType;

            (resolved ??= []).Add(new TranscriptAttachment(TranscriptIds.Sibling(messageId, "att:" + assetId), fileName, contentType, asset.Data));
        }

        return resolved;
    }

    /// Each part is a tick after the one before it, so a reader ordering by time keeps thinking, text and tool calls
    /// in order. Output rides the first part only, so the message is counted once; with no output count the line names
    /// no model either.
    static ProjectionResult AssistantMessage(JsonElement data, Envelope e, CopilotContext ctx) {
        var model        = data.Str("model");
        var outputTokens = data.Num("outputTokens");

        ctx.RunMessagesReportOutput = ctx.RunMessagesReportOutput == true || outputTokens is not null;

        var events = new List<CanonicalEvent>();

        if (data.Str("reasoningText") is { Length: > 0 } reasoning) {
            var thinking = new AssistantThinkingGenerated { Content = reasoning, Timestamp = Timestamp.FromDateTimeOffset(e.At) };
            TagAgent(thinking.Extensions, e.AgentId);
            events.Add(Event(thinking, TranscriptIds.Sibling(e.Id, "thinking"), e.At, e.RecordTs));
        }

        if (data.Str("content") is { Length: > 0 } text) {
            var at        = e.At.AddTicks(events.Count);
            var generated = new AssistantTextGenerated { Content = text, Timestamp = Timestamp.FromDateTimeOffset(at) };
            TagAgent(generated.Extensions, e.AgentId);
            events.Add(Event(generated, TranscriptIds.Sibling(e.Id, "text"), at, e.RecordTs));
        }

        if (data.Arr("toolRequests") is { } requests) {
            foreach (var request in requests.EnumerateArray()) {
                if (request.Str("toolCallId") is not { } callId || request.Str("name") is not { } name) continue;

                var call = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = CopilotToolKinds.Of(name) };
                if (Arguments(request) is { } arguments) call.Arguments = arguments;

                var at    = e.At.AddTicks(events.Count);
                var calls = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(at) };
                calls.ToolCalls.Add(call);
                TagAgent(calls.Extensions, e.AgentId);
                events.Add(Event(calls, TranscriptIds.Sibling(e.Id, "tool:" + callId), at, e.RecordTs));
            }
        }

        if (events.Count == 0) return ProjectionResult.Empty;

        if (outputTokens is { } output) {
            var usage = new TokenUsage { TotalTokens = output, OutputTokens = output };
            if (model is not null) usage.Model = model;
            events[0] = events[0] with { Usage = usage };
        }

        return ProjectionResult.Of(events);
    }

    /// An object from Copilot CLI and VS Code's Copilot harness, JSON text from VS Code's Local harness; anything else
    /// leaves the call without arguments.
    static Struct? Arguments(JsonElement request) {
        if (request.Obj("arguments") is { } arguments) return StructOf(arguments);
        if (request.Str("arguments") is not { Length: > 0 } text) return null;

        try {
            using var parsed = JsonDocument.Parse(text);

            return parsed.RootElement.IsObject ? StructOf(parsed.RootElement) : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// VS Code's Local harness writes no <c>result</c>, so its results carry the outcome only.
    static ProjectionResult ToolResult(JsonElement data, Envelope e) {
        if (data.Str("toolCallId") is not { } callId) return ProjectionResult.Empty;

        var received = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(e.At) };

        string? content  = null;
        string? detailed = null;

        if (data.Obj("result") is { } result) {
            content = result.Str("content");
            if (content is null && result.Prop("content") is { } raw && !raw.IsNull) content = raw.GetRawText();
            detailed = result.Str("detailedContent");
        }

        if (content is not null) received.Result = content;

        var success = data.Bool("success");
        var detail  = detailed == content ? null : detailed;
        if (success is not null || detail is not null || e.AgentId is not null)
            received.Extensions[CopilotExtension.Slug] = CopilotExtension.ToolResult(success, detail, e.AgentId);

        return One(received, e.Id, e);
    }

    static ProjectionResult UsageRecord(JsonElement data, Envelope e, CopilotContext ctx) {
        if (data.Obj("usage") is not { } usage || usage.Str("model") is not { Length: > 0 } model) return ProjectionResult.Empty;

        ctx.RunHasUsageRecords = true;

        var cacheRead  = usage.Num("cacheReadTokens") ?? 0;
        var cacheWrite = usage.Num("cacheWriteTokens") ?? 0;

        return One(new CopilotUsageBackfill {
            Model            = model,
            InputTokens      = NetInput(usage.Num("inputTokens") ?? 0, cacheRead, cacheWrite),
            OutputTokens     = usage.Num("outputTokens") ?? 0,
            CacheReadTokens  = cacheRead,
            CacheWriteTokens = cacheWrite,
            ReasoningTokens  = usage.Num("reasoningTokens"),
            SessionModel     = e.AgentId is null ? model : null,
        }, e.Id, e);
    }

    /// One backfill per model with tokens or billing, then the run's metrics. The totals equal the run's usage records,
    /// so after records (or watermarks saying records were counted) only billing is added; without them, input, cache
    /// and reasoning, and output only when the run's messages omitted it.
    static ProjectionResult Shutdown(JsonElement data, Envelope e, CopilotContext ctx) {
        var perModel       = new List<CanonicalEvent>();
        var envelopeAt     = -1;
        var isFork         = ctx.IsFork;
        var followsRecords = isFork || ctx.RunHasUsageRecords || ReportsAccountedUsage(data);
        var countsOutput   = !followsRecords && ctx.RunMessagesReportOutput == false;
        var currentModel   = data.Str("currentModel");

        if (data.Obj("modelMetrics") is { } modelMetrics) {
            foreach (var entry in modelMetrics.EnumerateObject()) {
                var model = entry.Name;
                if (model.Length == 0) continue;

                // A fork's per-model figures include its parent's and cannot be split; its usage records carry its own.
                if (isFork && model != currentModel) continue;

                var usage      = entry.Value.Obj("usage");
                var input      = usage?.Num("inputTokens") ?? 0;
                var cacheRead  = usage?.Num("cacheReadTokens") ?? 0;
                var cacheWrite = usage?.Num("cacheWriteTokens") ?? 0;
                var reasoning  = usage?.Num("reasoningTokens");

                var requests     = entry.Value.Obj("requests");
                var requestCount = requests?.Num("count");
                var requestCost  = requests is { } r ? Dbl(r, "cost") : null;
                var nanoAiu      = entry.Value.Num("totalNanoAiu");

                // Read from the gross figures. The first model with a token count gets the envelope id whatever its
                // billing, and shutdowns already stored hold that id, so this rule is frozen.
                var hasTokens  = usage is not null && (input != 0 || cacheRead != 0 || cacheWrite != 0 || (reasoning ?? 0) != 0);
                var hasBilling = (requestCount ?? 0) != 0 || (requestCost ?? 0) != 0 || (nanoAiu ?? 0) != 0;
                if (!hasTokens && !hasBilling) continue;

                var backfill = new CopilotUsageBackfill {
                    Model            = model,
                    InputTokens      = followsRecords ? 0 : NetInput(input, cacheRead, cacheWrite),
                    OutputTokens     = countsOutput ? usage?.Num("outputTokens") ?? 0 : 0,
                    CacheReadTokens  = followsRecords ? 0 : cacheRead,
                    CacheWriteTokens = followsRecords ? 0 : cacheWrite,
                    ReasoningTokens  = followsRecords ? null : reasoning,
                    RequestCount     = isFork ? null : requestCount,
                    RequestCost      = isFork ? null : requestCost,
                    NanoAiu          = isFork ? null : nanoAiu,
                    SessionModel     = model == currentModel ? model : null,
                };

                Guid id;
                if (hasTokens && envelopeAt < 0) {
                    id         = e.Id;
                    envelopeAt = perModel.Count;
                } else {
                    id = TranscriptIds.Sibling(e.Id, "usage:" + model);
                }

                perModel.Add(Event(backfill, id, e.At, e.RecordTs));
            }
        }

        var events = new List<CanonicalEvent>(perModel.Count + 1);
        if (envelopeAt >= 0) events.Add(perModel[envelopeAt]);
        for (var i = 0; i < perModel.Count; i++)
            if (i != envelopeAt) events.Add(perModel[i]);

        if (Metrics(data, ctx) is { } metrics) events.Add(Event(metrics, TranscriptIds.Sibling(e.Id, "session-metrics"), e.At, e.RecordTs));

        return ProjectionResult.Of(events);
    }

    /// A reported zero is a report; a run's metrics carry nothing only when the line reports none of them.
    static CopilotSessionMetrics? Metrics(JsonElement data, CopilotContext ctx) {
        var totalNano    = OwnSpend(data.Num("totalNanoAiu"), ctx.InheritedNanoAiu, ctx.IsFork);
        var totalPremium = OwnSpend(Dbl(data, "totalPremiumRequests"), ctx.InheritedPremiumRequests, ctx.IsFork);

        int?          linesAdded   = null, linesRemoved = null, filesCount = null;
        List<string>? files        = null;

        // A fork's code changes cannot be told from its parent's.
        if (!ctx.IsFork && data.Obj("codeChanges") is { } changes) {
            linesAdded   = (int?)changes.Num("linesAdded");
            linesRemoved = (int?)changes.Num("linesRemoved");

            if (changes.Arr("filesModified") is { } modified) {
                files = [];
                foreach (var file in modified.EnumerateArray())
                    if (file.IsString) files.Add(file.GetString()!);
                filesCount = files.Count;
            }
        }

        if (totalNano is null && totalPremium is null && linesAdded is null && linesRemoved is null && files is null) return null;

        return new CopilotSessionMetrics {
            TotalNanoAiu         = totalNano,
            TotalPremiumRequests = totalPremium,
            RunMarker            = ctx.RunMarker,
            CumulativeAcrossRuns = data.Obj("usageAccountingWatermarks") is null ? null : true,
            LinesAdded           = linesAdded,
            LinesRemoved         = linesRemoved,
            FilesModifiedCount   = filesCount,
            FilesModified        = files,
        };
    }

    /// One VS Code Local request's credits so far, which kcap reads from VS Code's chat log; it belongs to no run.
    static ProjectionResult RequestCredits(JsonElement data, Envelope e) {
        if (data.Str("requestId") is not { Length: > 0 } requestId || data.Num("totalNanoAiu") is not (>= 0 and var nano))
            return ProjectionResult.Empty;

        return One(new CopilotSessionMetrics { TotalNanoAiu = nano, RequestId = requestId }, e.Id, e);
    }

    /// A shutdown's watermarks count the usage records per source, and outlive a lost context.
    static bool ReportsAccountedUsage(JsonElement data) =>
        data.Obj("usageAccountingWatermarks") is { } watermarks
     && watermarks.EnumerateObject().Any(w => w.Value.IsNumber && w.Value.TryGetInt64(out var n) && n > 0);

    /// What a fork's cumulative total leaves once its parent's spend is off it; an axis whose parent figure is not
    /// known is not reported.
    static T? OwnSpend<T>(T? total, T? inherited, bool isFork) where T : struct, INumber<T> =>
        total is not { } reported || !isFork ? total
      : inherited is { } parent             ? T.Max(T.Zero, reported - parent)
      : null;

    static long NetInput(long input, long cacheRead, long cacheWrite) => Math.Max(0, input - cacheRead - cacheWrite);

    static void TagAgent(MapField<string, Struct> extensions, string? agentId) {
        if (agentId is not null) extensions[CopilotExtension.Slug] = CopilotExtension.Turn(agentId);
    }

    static double? Dbl(JsonElement obj, string property) =>
        obj.IsObject && obj.TryGetProperty(property, out var v) && v.IsNumber && v.TryGetDouble(out var d) ? d : null;

    static DateTimeOffset ParseTime(string? raw) =>
        raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : DateTimeOffset.MinValue;

    static string Canonical(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;

    static ProjectionResult One(object payload, Guid id, Envelope e) => ProjectionResult.Of([Event(payload, id, e.At, e.RecordTs)]);

    static CanonicalEvent Event(object payload, Guid id, DateTimeOffset at, string? recordTs) =>
        new(CanonicalEventTypes.Of(payload), payload, id, at, recordTs);

    readonly record struct Envelope(Guid Id, DateTimeOffset At, string? RecordTs, string? AgentId);
}
