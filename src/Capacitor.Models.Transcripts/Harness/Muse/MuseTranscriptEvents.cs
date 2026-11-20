using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Muse;

/// <summary>
/// Projects Muse Code's <c>session.jsonl</c>, a parent log and a subagent log alike, into canonical events. Only
/// durable <c>event</c> records of a run (<c>payload_type: runtime.session</c>, <c>payload.kind: run</c>) carry
/// conversation:
/// <list type="bullet">
///   <item><c>started</c>: the user prompt.</item>
///   <item><c>assistant_message_committed</c>, <c>reasoning_summary_committed</c>: text and thinking.</item>
///   <item><c>assistant_tool_calls_committed</c>: one event holding every call; <c>tool_result_batch_committed</c>: one event per result.</item>
///   <item><c>model_completed</c>: nothing; its usage waits for the run's next assistant event.</item>
///   <item><c>terminal</c>: a failed run as assistant text, any other run's leftover usage as a <see cref="MuseUsageBackfill"/>.</item>
/// </list>
/// A <c>retained_frame</c> line holds whole records as JSON strings, each projected as its own record. A record that
/// cannot be read costs only itself. The session's start and end reach the server from Muse's hooks, not this log.
/// </summary>
public sealed class MuseTranscriptEvents : ITranscriptProjection {
    public static readonly MuseTranscriptEvents Instance = new();

    MuseTranscriptEvents() { }

    /// Ids come from each record's own stream and id, so the context needs neither of these.
    public TranscriptContext CreateContext(string sessionId, string? agentId) => new MuseContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (MuseContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Prop("retained_marker") is not null) return ProjectionResult.Empty;

            if (root.Arr("children") is not { } children || root.Prop("retained_frame") is null)
                return ProjectionResult.Of(SafeRecord(root, receivedAt, ctx));

            var events = new List<CanonicalEvent>();
            foreach (var child in children.EnumerateArray()) {
                if (child.Str("record_json") is not { } json) continue;

                try {
                    using var inner = JsonDocument.Parse(json);
                    if (inner.RootElement.IsObject) events.AddRange(SafeRecord(inner.RootElement, receivedAt, ctx));
                } catch (JsonException) { }
            }

            return ProjectionResult.Of(events);
        }
    }

    static IReadOnlyList<CanonicalEvent> SafeRecord(JsonElement record, DateTimeOffset receivedAt, MuseContext ctx) {
        try {
            return Record(record, receivedAt, ctx);
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or InvalidJsonException or InvalidProtocolBufferException) {
            return [];
        }
    }

    static IReadOnlyList<CanonicalEvent> Record(JsonElement record, DateTimeOffset receivedAt, MuseContext ctx) {
        if (record.Str("record_type") != "event" || record.Str("durability") == "ephemeral") return [];
        if (record.Obj("stream")?.Str("id") is not { Length: > 0 } streamId || record.Str("id") is not { Length: > 0 } recordId) return [];
        if (record.Obj("payload") is not { } payload) return [];

        switch (record.Str("payload_type")) {
            case "runtime.session" when payload.Str("kind") == "run" && payload.Obj("event") is { } evt:
                return Run(new Envelope(record, payload, evt, receivedAt), streamId, recordId, ctx);

            case "run.model.configured" or "runtime.model_reconfigure.completed":
                if (payload.Obj("record")?.Str("model_id") is { Length: > 0 } model) ctx.CurrentModel = model;
                return [];

            default:
                return [];
        }
    }

    static IReadOnlyList<CanonicalEvent> Run(Envelope envelope, string streamId, string recordId, MuseContext ctx) {
        var evt   = envelope.RunEvent;
        var runId = envelope.RunId;
        var id    = TranscriptIds.MuseRecord(streamId, recordId);

        switch (evt.Str("kind")) {
            case "started" when evt.Str("prompt") is { Length: > 0 } prompt:
                return [envelope.Event(new UserMessageReceived { Content = prompt, Timestamp = envelope.PayloadTime() }, id)];

            case "assistant_message_committed" when evt.Str("text") is { Length: > 0 } text:
                return [envelope.Event(new AssistantTextGenerated { Content = text, Timestamp = envelope.PayloadTime() }, id, TakeUsage(runId, ctx))];

            // Never takes usage: a response's usage belongs to its text or tool calls.
            case "reasoning_summary_committed" when evt.Str("text") is { Length: > 0 } text:
                return [envelope.Event(new AssistantThinkingGenerated { Content = text, Timestamp = envelope.PayloadTime() }, id)];

            case "assistant_tool_calls_committed" when evt.Arr("tool_calls") is { } toolCalls:
                return ToolCalls(toolCalls, envelope, id, ctx);

            case "tool_result_batch_committed" when evt.Arr("results") is { } results:
                return ToolResults(results, envelope, streamId, recordId);

            case "model_completed" when runId is not null && evt.Obj("usage") is { } usage:
                RecordUsage(usage, evt.Str("model"), runId, recordId, ctx);
                return [];

            case "terminal":
                return Terminal(envelope, id, ctx);

            default:
                return [];
        }
    }

    static IReadOnlyList<CanonicalEvent> ToolCalls(JsonElement toolCalls, Envelope envelope, Guid id, MuseContext ctx) {
        var calls = new AssistantToolCallsGenerated { Timestamp = envelope.PayloadTime() };

        foreach (var tc in toolCalls.EnumerateArray()) {
            if (tc.Str("call_id") is not { Length: > 0 } callId || tc.Str("name") is not { Length: > 0 } name) continue;
            calls.ToolCalls.Add(new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = MuseToolKinds.Of(name), Arguments = ArgumentsOf(tc.Prop("args")) });
        }

        if (calls.ToolCalls.Count == 0) return [];

        calls.Extensions[MuseExtension.Slug] = MuseExtension.ToolCall(envelope.RunId);

        return [envelope.Event(calls, id, TakeUsage(envelope.RunId, ctx))];
    }

    /// One event per result, keyed by the batch record and the call's index; each is a tick after the one before it.
    /// The key is the server's, which its stored sessions dedup by: a position standing in for an omitted index can
    /// repeat an explicit one there too, so changing it needs both sides and a migration. Muse writes every index.
    static IReadOnlyList<CanonicalEvent> ToolResults(JsonElement results, Envelope envelope, string streamId, string recordId) {
        var events   = new List<CanonicalEvent>();
        var position = 0;

        foreach (var r in results.EnumerateArray()) {
            var index = r.Num("tool_call_index") ?? position;
            position++;

            if (r.Str("tool_call_id") is not { Length: > 0 } callId) continue;

            var text   = r.Str("text") ?? "";
            var result = new ToolResultReceived { CallId = callId, Result = text, Timestamp = envelope.PayloadTime(events.Count) };
            result.Extensions[MuseExtension.Slug] = MuseExtension.ToolResult(text);

            events.Add(envelope.Event(result, TranscriptIds.MuseToolResult(streamId, recordId, index), ticks: events.Count));
        }

        return events;
    }

    /// A failed run surfaces as assistant text. Any other run that ends with usage still pending carries it on a
    /// backfill: its last response reported usage after its only text, and nothing else will count it.
    static IReadOnlyList<CanonicalEvent> Terminal(Envelope envelope, Guid id, MuseContext ctx) {
        if (envelope.RunEvent.Str("terminal") == "failed") {
            var content = envelope.RunEvent.Str("reason") is { Length: > 0 } reason ? $"Turn failed: {reason}" : "Turn failed";
            var failed  = new AssistantTextGenerated { Content = content, Timestamp = envelope.PayloadTime() };

            return [envelope.Event(failed, id, TakeUsage(envelope.RunId, ctx)) with { VendorMetadataValues = envelope.Values(turnFailed: true) }];
        }

        return TakeUsage(envelope.RunId, ctx) is { } usage ? [envelope.Event(new MuseUsageBackfill(), id, usage)] : [];
    }

    /// <c>input_tokens</c> includes <c>cached_tokens</c> and <c>output_tokens</c> includes <c>reasoning_tokens</c>, as
    /// in OpenAI's accounting. Reports with no assistant event between them add up; one already folded, re-delivered
    /// when the watcher rewinds to its last stored line, is skipped.
    static void RecordUsage(JsonElement usage, string? model, string runId, string recordId, MuseContext ctx) {
        var hasPrior = ctx.PendingUsage.TryGetValue(runId, out var prior);
        if (hasPrior && prior!.ReportIds.Contains(recordId)) return;

        var cached    = usage.Num("cached_tokens") ?? usage.Num("cache_read_tokens") ?? 0;
        var input     = Math.Max(0, (usage.Num("input_tokens") ?? 0) - cached);
        var output    = usage.Num("output_tokens") ?? 0;
        var reasoning = usage.Num("reasoning_tokens") ?? 0;
        var named     = model ?? ctx.CurrentModel;

        ctx.PendingUsage[runId] = hasPrior
            ? new MusePendingUsage(
                prior!.Input + input,
                prior.Output + output,
                prior.CacheRead + cached,
                prior.Reasoning + reasoning,
                named ?? prior.Model,
                new HashSet<string>(prior.ReportIds, StringComparer.Ordinal) { recordId })
            : new MusePendingUsage(input, output, cached, reasoning, named, new HashSet<string>(StringComparer.Ordinal) { recordId });
    }

    /// The run's pending usage, removed so it is counted once. The total is the three additive counts; Muse reports
    /// no cache writes.
    static TokenUsage? TakeUsage(string? runId, MuseContext ctx) {
        if (runId is null || !ctx.PendingUsage.Remove(runId, out var pending)) return null;

        var usage = new TokenUsage {
            TotalTokens       = pending.Input + pending.Output + pending.CacheRead,
            InputTokens       = pending.Input,
            OutputTokens      = pending.Output,
            CachedInputTokens = pending.CacheRead,
            ReasoningTokens   = pending.Reasoning,
        };
        if (pending.Model is { } model) usage.Model = model;

        return usage;
    }

    /// A call's <c>args</c> is a JSON string; anything but an object is kept whole under <c>arguments</c>.
    static Struct ArgumentsOf(JsonElement? args) {
        if (args is { IsObject: true } obj) return StructOf(obj);
        if (args is not { } s || !s.IsString || s.GetString() is not { } raw) return new Struct();

        try {
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.IsObject) return StructOf(parsed.RootElement);
        } catch (JsonException) { }

        return Wrap("arguments", raw);
    }

    /// What every event of one run record shares: its time and its metadata.
    readonly struct Envelope {
        const long MaxUnixMicros = 253_402_300_799_999_999;

        readonly DateTimeOffset?                     _recorded;
        readonly DateTimeOffset                      _at;
        readonly string?                             _recordTimestamp;
        readonly long?                               _sequence;
        readonly IReadOnlyDictionary<string, string>? _strings;

        public Envelope(JsonElement record, JsonElement payload, JsonElement evt, DateTimeOffset receivedAt) {
            RunEvent         = evt;
            RunId            = payload.Str("run_id");
            _recorded        = RecordedAt(record.Num("recorded_at"));
            _at              = _recorded ?? receivedAt;
            _recordTimestamp = _recorded?.ToString("O", CultureInfo.InvariantCulture);
            _sequence        = record.Num("sequence");

            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (RunId is { } runId) strings[MuseMetadataKeys.RunId] = runId;
            if ((payload.Str("task_id") ?? evt.Str("task_id")) is { } taskId) strings[MuseMetadataKeys.TaskId] = taskId;
            _strings = strings.Count > 0 ? strings : null;
        }

        public JsonElement RunEvent { get; }
        public string?     RunId { get; }

        /// The record's own time, a tick per part after the first; unset when the record has none, as the server stores it.
        public Timestamp? PayloadTime(int ticks = 0) => _recorded is { } at ? Timestamp.FromDateTimeOffset(at.AddTicks(ticks)) : null;

        public IReadOnlyDictionary<string, object>? Values(bool turnFailed = false) {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (_sequence is { } sequence) values[MuseMetadataKeys.Sequence] = sequence;
            if (turnFailed) values[MuseMetadataKeys.TurnFailed] = true;
            return values.Count > 0 ? values : null;
        }

        public CanonicalEvent Event(object payload, Guid id, TokenUsage? usage = null, int ticks = 0) =>
            new(CanonicalEventTypes.Of(payload), payload, id, _at.AddTicks(ticks), _recordTimestamp, Usage: usage) {
                VendorMetadata       = _strings,
                VendorMetadataValues = Values(),
            };

        /// <c>recorded_at</c> is epoch microseconds; null when absent or beyond what a DateTimeOffset holds.
        static DateTimeOffset? RecordedAt(long? micros) =>
            micros is { } us and >= 0 and <= MaxUnixMicros
                ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000).AddTicks(us % 1000 * 10)
                : null;
    }
}
