using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;
using static Capacitor.Models.Transcripts.Harness.OpenCode.OpenCodeTranscriptEvents;

namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// <summary>
/// Projects one OpenCode v2 session message: <c>{id, type, ...}</c>, as the session's context returns it to the kcap
/// plugin and as import rebuilds it from <c>session_message</c> and <c>session_part</c>.
/// <list type="bullet">
///   <item>A <c>user</c> message is one <see cref="UserMessageReceived"/> on its message id.</item>
///   <item>An <c>assistant</c> message is one event per <c>reasoning</c> or <c>text</c> part, on the message id and the
///   part's position, and two per settled <c>tool</c> part, on the message and call id, in content order and each a tick
///   after the one before; the message's usage rides on the first.</item>
///   <item>A settled <c>shell</c> message, a command the user ran, is a tool call and its result.</item>
/// </list>
/// Every other message type emits nothing, and so does a tool that has not settled.
/// </summary>
static class OpenCodeV2Message {
    const string ShellTool = "shell";

    public static ProjectionResult Project(JsonElement root, DateTimeOffset receivedAt) {
        if (root.Str("type") is not ("user" or "assistant" or "shell") || root.Str("id") is not { Length: > 0 } id)
            return ProjectionResult.Empty;
        if (!TryCreated(root, receivedAt, out var at, out var recordTs)) return ProjectionResult.Reject("time.created is out of range");

        return root.Str("type") switch {
            "user"      => User(root, id, at, recordTs),
            "assistant" => Assistant(root, id, at, recordTs),
            _           => Shell(root, id, at, recordTs),
        };
    }

    static ProjectionResult User(JsonElement root, string id, DateTimeOffset at, string? recordTs) {
        if (root.Str("text") is not { Length: > 0 } text) return ProjectionResult.Empty;

        var message = new UserMessageReceived { Content = text, Timestamp = Timestamp.FromDateTimeOffset(at) };
        return ProjectionResult.Of([Event(message, TranscriptIds.OpenCode(id), at, recordTs)]);
    }

    static ProjectionResult Assistant(JsonElement root, string id, DateTimeOffset at, string? recordTs) {
        if (root.Arr("content") is not { } content) return ProjectionResult.Empty;

        var events   = new List<CanonicalEvent>();
        var position = -1;
        foreach (var part in content.EnumerateArray()) {
            position++;
            var partAt = at.AddTicks(events.Count);
            switch (part.Str("type")) {
                case "reasoning" when part.Str("text") is { Length: > 0 } thought:
                    var thinking = new AssistantThinkingGenerated { Content = thought, Timestamp = Timestamp.FromDateTimeOffset(partAt) };
                    events.Add(Event(thinking, TranscriptIds.OpenCodeContent(id, position), partAt, recordTs));
                    break;
                case "text" when part.Str("text") is { Length: > 0 } text:
                    var reply = new AssistantTextGenerated { Content = text, Timestamp = Timestamp.FromDateTimeOffset(partAt) };
                    events.Add(Event(reply, TranscriptIds.OpenCodeContent(id, position), partAt, recordTs));
                    break;
                case "tool":
                    Tool(part, id, at, recordTs, events);
                    break;
            }
        }

        if (events.Count == 0) return ProjectionResult.Empty;
        if (Usage(root.Obj("model")?.Str("id"), root.Obj("tokens")) is { } usage)
            events[0] = events[0] with { Usage = usage.Usage, CacheCreationTokens = usage.CacheWrite };
        return ProjectionResult.Of(events);
    }

    static void Tool(JsonElement part, string messageId, DateTimeOffset at, string? recordTs, List<CanonicalEvent> events) {
        if (part.Str("id") is not { Length: > 0 } callId || part.Str("name") is not { } name) return;
        if (part.Obj("state") is not { } state || state.Str("status") is not ("completed" or "error") || state.Str("status") is not { } status) return;

        var call = new ToolCallInfo { CallId = callId, ToolName = name, ToolKind = OpenCodeToolKinds.Of(name) };
        if (state.Obj("input") is { } input) call.Arguments = StructOf(input);

        var output = status == "error" ? state.Obj("error")?.Str("message") ?? ContentText(state) : ContentText(state);
        var child  = name == "subagent" ? state.Obj("metadata")?.Str("sessionID") : null;
        Settled(call, child, status, output, TranscriptIds.OpenCodeCall(messageId, callId), TranscriptIds.OpenCodeCallResult(messageId, callId), at, recordTs, events);
    }

    /// A command the user ran, once it ended: exited with status 0 it completed, anything else is an error.
    static ProjectionResult Shell(JsonElement root, string id, DateTimeOffset at, string? recordTs) {
        if (root.Str("shellID") is not { Length: > 0 } shellId || root.Str("command") is not { } command) return ProjectionResult.Empty;
        if (root.Str("status") is not ("exited" or "timeout" or "killed") || root.Str("status") is not { } shellStatus) return ProjectionResult.Empty;

        var call = new ToolCallInfo { CallId = shellId, ToolName = ShellTool, ToolKind = OpenCodeToolKinds.Of(ShellTool) };
        call.Arguments = Wrap("command", command);

        var status = shellStatus == "exited" && root.Num("exit") == 0 ? "completed" : "error";
        var events = new List<CanonicalEvent>();
        Settled(call, null, status, root.Obj("output")?.Str("output"), TranscriptIds.OpenCodeCall(id, shellId), TranscriptIds.OpenCodeCallResult(id, shellId), at, recordTs, events);
        return ProjectionResult.Of(events);
    }

    static void Settled(
            ToolCallInfo call, string? childSessionId, string status, string? output, Guid callEventId, Guid resultEventId,
            DateTimeOffset at, string? recordTs, List<CanonicalEvent> events
        ) {
        var callAt = at.AddTicks(events.Count);
        var calls  = new AssistantToolCallsGenerated { Timestamp = Timestamp.FromDateTimeOffset(callAt) };
        calls.ToolCalls.Add(call);
        if (childSessionId is { Length: > 0 }) calls.Extensions[OpenCodeExtension.Slug] = OpenCodeExtension.ToolCall(childSessionId);
        events.Add(Event(calls, callEventId, callAt, recordTs));

        var resultAt = at.AddTicks(events.Count);
        var result   = new ToolResultReceived { CallId = call.CallId, Timestamp = Timestamp.FromDateTimeOffset(resultAt) };
        if (output is { Length: > 0 }) result.Result = output;
        result.Extensions[OpenCodeExtension.Slug] = OpenCodeExtension.ToolResult(status, null);
        events.Add(Event(result, resultEventId, resultAt, recordTs));
    }

    /// The text a tool gave the model; a file it returned is not text.
    static string? ContentText(JsonElement state) => state.Arr("content") is { } content ? JoinTextBlocks(content, "text") : null;
}
