using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>Continues a session whose agent is gone. Kept out of every tier-gated server, because
/// continuing must work on every plan, and out of kcap-sessions, which reviewers auto-approve whole.</summary>
sealed class McpHandoffServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    internal const string ToolName = "continue_session";

    const string CurrentSessionArgument = "current_session_id";

    internal const string NoCurrentSessionMessage =
        "Error: no current session. Pass current_session_id with this session's id, or run continue_session inside the harness session that takes over (CLAUDE_CODE_SESSION_ID, GROK_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).";

    public Task<int> RunAsync() =>
        McpServerHost.RunAsync(
            new("kcap-handoff", "handoff", ServerInstructions, BuildToolsList()),
            new(config, profiles, tokens, http, startup, time),
            // Per call: a hosted agent is bound to its session only after this server has started.
            (id, request, client, baseUrl) => HandleToolCallAsync(
                id, request, client, baseUrl, WorkContextIds.CanonicalSessionId(HarnessRequesterContext.Resolve().SessionId)));

    internal const string ServerInstructions =
        "Use continue_session when the user asks you to continue, resume or pick up another session's work — " +
        "typically one whose agent was killed. It attaches this session to that session's work items and " +
        "unfinished plans. If it refuses because the session may still be running, ask the user before " +
        "retrying with force: true. Then check the working tree before resuming: a task left in progress may be half-done.";

    internal async Task<string> HandleToolCallAsync(JsonNode id, JsonObject request, HttpClient client, string baseUrl, string? currentSessionId) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) return McpResponses.Error(id, -32602, "Missing params.name");
        if (toolName != ToolName) return McpResponses.ToolResult(id, $"Error: Unknown tool: {toolName}", isError: true);

        try {
            if (McpToolArguments.OptionalString(arguments, CurrentSessionArgument) is { } given)
                currentSessionId = WorkContextIds.CanonicalSessionId(given) ?? given;

            if (currentSessionId is null) return McpResponses.ToolResult(id, NoCurrentSessionMessage, isError: true);

            var previous = McpToolArguments.RequireString(arguments, "session_id");
            var force    = OptionalBool(arguments, "force");

            var result = await new SessionTakeover(AgentSessions.OnThisMachine(config), time)
                .RunAsync(client, baseUrl, previous, currentSessionId, force ? TakeoverAdmission.Forced : TakeoverAdmission.Judged);

            return result switch {
                TakeoverResult.Completed { CredentialRejected: true } c
                                              => McpResponses.ToolResult(id, $"{c.Outcome.ToJsonString()}\n\n{await UnauthorizedNoticeAsync(baseUrl)}", isError: true),
                TakeoverResult.Completed c    => McpResponses.ToolResult(id, c.Outcome.ToJsonString(), isError: c.Unsuccessful),
                TakeoverResult.Refused r      => McpResponses.ToolResult(id, $"Error: {r.Reason}", isError: true),
                TakeoverResult.Failed f       => McpResponses.ToolResult(id, $"Error: {f.Reason}", isError: true),
                TakeoverResult.Interrupted i  => McpResponses.ToolResult(id, $"Error: the takeover was interrupted ({i.Code}).\n\n{i.Outcome.ToJsonString()}", isError: true),
                TakeoverResult.Unauthorized   => McpResponses.ToolResult(id, await UnauthorizedNoticeAsync(baseUrl), isError: true),
                _                             => throw new InvalidOperationException(result.GetType().Name),
            };
        } catch (ArgumentException ex) {
            return McpResponses.ToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return McpResponses.ToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    Task<string> UnauthorizedNoticeAsync(string baseUrl) => AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time);

    static bool OptionalBool(JsonObject? args, string name) =>
        args?[name] switch {
            null                                       => false,
            JsonValue v when v.TryGetValue(out bool b) => b,
            _                                          => throw new ArgumentException($"'{name}' must be a boolean."),
        };

    /// <summary>Null for a present but wrong-shaped <c>method</c> instead of throwing — a malformed
    /// request must yield an invalid-request response, never terminate the stdio loop.</summary>
    internal static string? DecodeMethod(JsonObject request) {
        try {
            return request["method"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    internal static McpTool[] BuildToolsList() => [
        new(ToolName,
            "Continue another session's work: attach this session to its work items and unfinished plans, so "
          + "the app shows them here. Refuses while that session may still be running — a live agent process "
          + "on this machine, or a session active in the last hour that did not run here; ask the user before "
          + "retrying with force. Work items are skipped, not failed, on a plan without them. Returns what was "
          + "attached and skipped, and which plan is now current.",
            new("object", new() {
                ["session_id"] = new("string", "The session whose work this session takes over."),
                ["force"]      = new("boolean", "Take over even when that session looks live. Only after the user confirms."),
                [CurrentSessionArgument] = new("string",
                    "This session's id — the one taking over. Defaults to the harness session this server runs in; "
                  + "pass it when the harness does not expose it."),
            }, ["session_id"]), McpToolAnnotations.Additive)
    ];
}
