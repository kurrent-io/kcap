using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Pi;

/// <summary>
/// Projects a Pi session file into canonical events. An entry's <c>id</c> is unique only within its file, and Pi
/// branches in place, so the file interleaves branches: every event carries its entry's tree node under
/// <see cref="PiTreeKeys"/>. Pi's format is lightly documented and changes often, so an unknown type or role is
/// ignored rather than rejected.
/// </summary>
public sealed class PiTranscriptEvents : ITranscriptProjection {
    public static readonly PiTranscriptEvents Instance = new();

    PiTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new PiTranscriptContext(sessionId);

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");

            var tree = (PiTranscriptContext)context;
            if (root.Str("type") is not { } type) return ProjectionResult.Empty;

            var entryId  = root.Str("id");
            var parentId = root.Str("parentId");
            if (entryId is not null) tree.Record(entryId, parentId);

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var entry = new PiLineEntry(line, tree.SessionId, entryId, parentId, at, recordTimestamp,
                NodeOf(entryId, parentId, tree.NearestEmittedAncestor(parentId)));

            IReadOnlyList<CanonicalEvent> events = type switch {
                "session"        => [Session(root, entry)],
                "message"        => Message(root, entry),
                "compaction"     => [Compaction(root, entry)],
                "branch_summary" => BranchSummary(root, entry),
                _                => [],
            };

            if (events.Count > 0 && entryId is not null) tree.MarkEmitted(entryId);

            return ProjectionResult.Of(events);
        }
    }

    static Dictionary<string, string>? NodeOf(string? entryId, string? parentId, string? renderParent) {
        if (entryId is null && parentId is null && renderParent is null) return null;

        var node = new Dictionary<string, string>(3, StringComparer.Ordinal);
        if (entryId is not null) node[PiTreeKeys.EntryId] = entryId;
        if (parentId is not null) node[PiTreeKeys.ParentId] = parentId;
        if (renderParent is not null) node[PiTreeKeys.RenderParent] = renderParent;
        return node;
    }

    // ── session header ──────────────────────────────────────────────────────────────────────────

    /// The header is keyed by the session id written in it, not the stream's: the stream's is the canonical form,
    /// so the two can differ.
    static CanonicalEvent Session(JsonElement root, PiLineEntry entry) {
        var started = new SessionStarted { Timestamp = Timestamp.FromDateTimeOffset(entry.At) };
        started.Extensions[PiExtension.Slug] = PiExtension.Session(root.Str("cwd"), (int?)root.Num("version"), root.Str("parentSession"));

        var id = root.Str("id") is { Length: > 0 } sessionId
            ? TranscriptIds.PiEntry(sessionId, "session", "SessionStarted")
            : TranscriptIds.PiHeaderLine(entry.Line);

        return entry.Event(started, id);
    }

    // ── compaction and branch summaries ─────────────────────────────────────────────────────────

    /// Pi's summary is plaintext, so the whole entry is the replacement history.
    static CanonicalEvent Compaction(JsonElement root, PiLineEntry entry) =>
        entry.Event(CompactionHistoryBound.Build(root, encryptedContent: null), entry.Id("compaction"));

    static IReadOnlyList<CanonicalEvent> BranchSummary(JsonElement root, PiLineEntry entry) {
        if (root.Str("summary") is not { } summary || string.IsNullOrWhiteSpace(summary)) return [];

        var text = new AssistantTextGenerated { Content = summary, Timestamp = Timestamp.FromDateTimeOffset(entry.At) };
        text.Extensions[PiExtension.Slug] = PiExtension.BranchSummary(
            root.Str("fromId"), entry.ParentId, summary, Present(root.Prop("details")), root.Bool("fromHook"));

        return [entry.Event(text, entry.Id("branch_summary"))];
    }

    // ── messages ────────────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<CanonicalEvent> Message(JsonElement root, PiLineEntry entry) {
        if (root.Obj("message") is not { } message) return [];

        return message.Str("role") switch {
            "user"          => User(message, entry),
            "assistant"     => Assistant(message, entry),
            "toolResult"    => ToolResult(message, entry),
            "bashExecution" => BashExecution(message, entry),
            _               => [],
        };
    }

    static IReadOnlyList<CanonicalEvent> User(JsonElement message, PiLineEntry entry) {
        if (ContentText(message.Prop("content")) is not { Length: > 0 } text) return [];

        return [entry.Event(new UserMessageReceived { Content = text, Timestamp = Timestamp.FromDateTimeOffset(entry.At) }, entry.Id("user"))];
    }

    /// A block's id names its index in the content array, so two text blocks of one message keep distinct ids.
    static IReadOnlyList<CanonicalEvent> Assistant(JsonElement message, PiLineEntry entry) {
        if (message.Arr("content") is not { } content) return [];

        var parts = new List<(IMessage Payload, Guid Id)>();
        var index = -1;

        foreach (var block in content.EnumerateArray()) {
            index++;
            var ts = Timestamp.FromDateTimeOffset(entry.AtTicks(parts.Count));

            switch (block.Str("type")) {
                case "thinking" when block.Str("thinking") is { Length: > 0 } thinking:
                    parts.Add((new AssistantThinkingGenerated { Content = thinking, Timestamp = ts }, entry.Id(BlockKind(index, "thinking"))));
                    break;

                case "text" when block.Str("text") is { Length: > 0 } text:
                    parts.Add((new AssistantTextGenerated { Content = text, Timestamp = ts }, entry.Id(BlockKind(index, "text"))));
                    break;

                case "toolCall" when block.Str("id") is { } callId && block.Str("name") is { } name:
                    var call = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = PiToolKinds.Of(name) };
                    if (block.Obj("arguments") is { } arguments) call.Arguments = StructOf(arguments);

                    var calls = new AssistantToolCallsGenerated { Timestamp = ts };
                    calls.ToolCalls.Add(call);
                    parts.Add((calls, entry.Id(BlockKind(index, "tool:" + callId))));
                    break;
            }
        }

        if (parts.Count == 0) return [];

        var model = message.Str("model");
        var usage = message.Obj("usage");

        var turn = PiTurn(message, entry.ParentId, model, usage);
        if (turn is not null) SchemaExtensions.Of(parts[0].Payload)![PiExtension.Slug] = turn;

        var (tokens, cacheWrite) = Usage(usage, model);
        var events = new List<CanonicalEvent>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
            events.Add(i == 0
                ? entry.Event(parts[i].Payload, parts[i].Id, i, tokens, cacheWrite)
                : entry.Event(parts[i].Payload, parts[i].Id, i));

        return events;
    }

    static string BlockKind(int index, string kind) => string.Create(CultureInfo.InvariantCulture, $"assistant:{index}:{kind}");

    static Struct? PiTurn(JsonElement message, string? parentId, string? model, JsonElement? usage) {
        var provider   = message.Str("provider");
        var stopReason = message.Str("stopReason");
        var cost       = usage?.Obj("cost") is { } c ? Fraction(c, "total") : null;

        return provider is null && model is null && stopReason is null && parentId is null && cost is null
            ? null
            : PiExtension.Turn(provider, model, stopReason, parentId, cost);
    }

    /// Pi reports every token bucket per message. The total is the sum of the buckets present, not Pi's own
    /// <c>totalTokens</c>; a usage with no bucket, even one naming a model, records nothing.
    static (TokenUsage? Usage, long? CacheWrite) Usage(JsonElement? usage, string? model) {
        if (usage is not { } u) return (null, null);

        var input      = u.Num("input");
        var output     = u.Num("output");
        var cacheRead  = u.Num("cacheRead");
        var cacheWrite = u.Num("cacheWrite");
        if (input is null && output is null && cacheRead is null && cacheWrite is null) return (null, null);

        var tokens = new TokenUsage { TotalTokens = (input ?? 0) + (output ?? 0) + (cacheRead ?? 0) + (cacheWrite ?? 0) };
        if (input is not null) tokens.InputTokens = input.Value;
        if (output is not null) tokens.OutputTokens = output.Value;
        if (cacheRead is not null) tokens.CachedInputTokens = cacheRead.Value;
        if (model is not null) tokens.Model = model;

        return (tokens, cacheWrite);
    }

    static IReadOnlyList<CanonicalEvent> ToolResult(JsonElement message, PiLineEntry entry) {
        if (message.Str("toolCallId") is not { Length: > 0 } callId) return [];

        var result = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(entry.At) };
        if (ContentText(message.Prop("content")) is { } content) result.Result = content;

        var success = message.Bool("isError") is { } isError ? !isError : (bool?)null;
        var toolName    = message.Str("toolName");
        var details     = message.Prop("details");
        var detailsText = DetailsText(details);
        var detailsJson = DetailsJson(toolName, details);
        var subagent    = Subagent(toolName, details);

        if (success is not null || toolName is not null || detailsText is not null || detailsJson is not null || subagent is not null)
            result.Extensions[PiExtension.Slug] = PiExtension.ToolResult(success, toolName, detailsText, detailsJson, subagent);

        return [entry.Event(result, entry.Id("toolResult"))];
    }

    /// The user's own <c>!</c> shell. Pi gives the command no call id, so one is made from the entry's.
    static IReadOnlyList<CanonicalEvent> BashExecution(JsonElement message, PiLineEntry entry) {
        if (message.Str("command") is not { Length: > 0 } command) return [];

        var callId = "pi-bash-" + (entry.EntryId ?? "x");
        var calls  = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(entry.At) };
        calls.ToolCalls.Add(new ToolCallInfo {
            CallId = callId, ToolName = "bash", ToolKind = PiToolKinds.Of("bash"), Arguments = Wrap("command", command),
        });

        var exitCode = message.Num("exitCode");
        var result   = new ToolResultReceived { CallId = callId, Timestamp = Timestamp.FromDateTimeOffset(entry.AtTicks(1)) };
        if (message.Str("output") is { } output) result.Result = output;
        result.Extensions[PiExtension.Slug] = PiExtension.ToolResult(
            success: exitCode is null ? null : exitCode == 0, toolName: "bash", command: command, exitCode: exitCode, userBash: true);

        return [entry.Event(calls, entry.Id("bash:call")), entry.Event(result, entry.Id("bash:result"), 1)];
    }

    // ── tool-result details ─────────────────────────────────────────────────────────────────────

    static string? DetailsText(JsonElement? details) => details?.ValueKind switch {
        JsonValueKind.String                                              => details.Value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => details.Value.GetRawText(),
        _                                                                 => null,
    };

    /// Structured details verbatim, except a subagent's: each result's <c>messages</c>, the child's whole
    /// transcript, is dropped from the copy.
    static JsonElement? DetailsJson(string? toolName, JsonElement? details) {
        if (details is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } d) return null;
        if (!IsSubagent(toolName, d) || JsonNode.Parse(d.GetRawText()) is not JsonObject copy) return d;

        if (copy["results"] is JsonArray results)
            foreach (var result in results.OfType<JsonObject>()) result.Remove("messages");

        using var trimmed = JsonDocument.Parse(copy.ToJsonString());
        return trimmed.RootElement.Clone();
    }

    static bool IsSubagent(string? toolName, JsonElement details) =>
        details.IsObject
     && (string.Equals(toolName, "subagent", StringComparison.OrdinalIgnoreCase) || details.Str("mode") is not null || details.Arr("results") is not null);

    static Struct? Subagent(string? toolName, JsonElement? details) {
        if (details is not { } d || !IsSubagent(toolName, d)) return null;

        int?    resultCount = null;
        int     succeeded   = 0, failed = 0;
        long?   input       = null, output = null, cacheRead = null, cacheWrite = null, contextTokens = null, turns = null;
        double? cost        = null;

        if (d.Arr("results") is { } results) {
            resultCount = 0;

            foreach (var result in results.EnumerateArray()) {
                if (!result.IsObject) continue;

                resultCount++;
                if (IsFailedSubagentResult(result)) failed++;
                else succeeded++;

                if (result.Obj("usage") is not { } usage) continue;

                input         = Add(input, usage.Num("input"));
                output        = Add(output, usage.Num("output"));
                cacheRead     = Add(cacheRead, usage.Num("cacheRead"));
                cacheWrite    = Add(cacheWrite, usage.Num("cacheWrite"));
                contextTokens = Add(contextTokens, usage.Num("contextTokens"));
                turns         = Add(turns, usage.Num("turns"));
                cost          = Add(cost, Fraction(usage, "cost"));
            }
        }

        var mode             = d.Str("mode");
        var agentScope       = d.Str("agentScope");
        var projectAgentsDir = d.Str("projectAgentsDir");

        if (mode is null && agentScope is null && projectAgentsDir is null && resultCount is null
         && input is null && output is null && cacheRead is null && cacheWrite is null && contextTokens is null && turns is null && cost is null)
            return null;

        return PiExtension.Subagent(mode, agentScope, projectAgentsDir, resultCount,
            resultCount is null ? null : succeeded, resultCount is null ? null : failed,
            input, output, cacheRead, cacheWrite, contextTokens, turns, cost);
    }

    static bool IsFailedSubagentResult(JsonElement result) {
        if (result.Num("exitCode") is { } exitCode) return exitCode != 0;
        if (result.Str("errorMessage") is { Length: > 0 }) return true;

        return result.Str("stopReason") is "error" or "aborted";
    }

    static long? Add(long? sum, long? value) => value is null ? sum : (sum ?? 0) + value.Value;

    static double? Add(double? sum, double? value) => value is null ? sum : (sum ?? 0) + value.Value;

    // ── shared helpers ──────────────────────────────────────────────────────────────────────────

    static string? ContentText(JsonElement? content) {
        if (content is not { } c) return null;
        if (c.IsString) return c.GetString();
        if (!c.IsArray) return null;

        var sb = new StringBuilder();
        foreach (var block in c.EnumerateArray()) {
            if (block.Str("type") != "text" || block.Str("text") is not { Length: > 0 } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// A fractional number such as a dollar cost, which <c>Num</c> would refuse.
    static double? Fraction(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.IsNumber && v.TryGetDouble(out var n) ? n : null;

    static JsonElement? Present(JsonElement? element) => element is { ValueKind: not JsonValueKind.Null } e ? e : null;
}
