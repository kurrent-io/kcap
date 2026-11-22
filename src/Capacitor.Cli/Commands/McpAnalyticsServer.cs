using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap mcp analytics` — two read-only tools over the bearer-authed /api/analytics views:
/// the agent fetches the schema document, writes SQL, and self-repairs from the server's
/// rejection reasons. Structure cloned from McpMemoryServer.
/// </summary>
sealed class McpAnalyticsServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, GitProviderRouter router, WorkingDirectory workdir, TimeProvider time) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    internal const string NotSupportedMessage =
        "This server does not support analytics queries — upgrade kcap-server.";

    internal const string TimedOutMessage = "Query timed out — narrow the date range or aggregate.";

    internal const string SchemaTimedOutMessage = "Schema fetch timed out — retry.";

    // The query hint ("narrow the date range") is nonsense for the schema fetch, which has no
    // query to narrow — pick the message that matches the timed-out tool.
    static string TimeoutHintFor(string toolName) =>
        toolName == "get_analytics_schema" ? SchemaTimedOutMessage : TimedOutMessage;

    public Task<int> RunAsync() {
        var repository = new CwdRepository(config, workdir.Path, router, time);

        return McpServerHost.RunAsync(
            new("kcap-analytics", "analytics", ServerInstructions, BuildToolsList()) { RejectsNonStringMethod = false },
            new(config, profiles, tokens, http, startup, time),
            async (id, request, client, baseUrl) => await HandleToolCallAsync(
                id, request, client, baseUrl, ReadsTheCwdRepository(request) ? await repository.GetHashAsync(client, baseUrl) : null));
    }

    internal const string ServerInstructions =
        "Query kcap's AI coding-agent analytics (sessions, tool/skill/token usage, cost, " +
        "commits, PRs, evals) with governed read-only SQL. Call get_analytics_schema once to learn the " +
        "views, then write Postgres SELECTs for query_analytics. Rejections include the reason — fix the " +
        "SQL and retry.";

    /// <summary>Whether the call's tool can read the working directory's repository: a query can, the schema cannot, and
    /// only a reader is worth the lookup a repository with no remote costs.</summary>
    internal static bool ReadsTheCwdRepository(JsonObject callRequest) =>
        (callRequest["params"] as JsonObject)?["name"] is JsonValue name && name.TryGetValue(out string? tool) && tool == "query_analytics";

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash
        ) {
        // `as JsonObject` (not AsObject(), which throws) so a wrong-SHAPED params/arguments —
        // e.g. an array where an object is expected — degrades to null and becomes an actionable
        // error below, rather than an InvalidOperationException masked as a generic internal error.
        var paramsNode = request["params"] as JsonObject;
        var arguments  = paramsNode?["arguments"] as JsonObject;

        // Read params.name tolerantly: a wrong-typed value (LLM clients send them) must yield a
        // clean protocol error, not a throw that the outer catch masks as an internal error.
        if (!TryGetStringArg(paramsNode, "name", out var toolName)) {
            return McpResponses.Error(id, -32602, "Missing or invalid params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "get_analytics_schema" => await client.GetAsync($"{baseUrl}/api/analytics/schema"),
                "query_analytics"      => await client.PostAsync($"{baseUrl}/api/analytics/query", ToJsonContent(BuildQueryBody(arguments, cwdRepoHash))),
                _                      => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            // The 401 wording is store-aware (a locally-valid stored credential the
            // server rejects must not read as "not logged in"), which needs an async store
            // read — resolved here so MapResponse stays a pure, unit-testable mapper (its own
            // 401 arm remains as the fallback wording).
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return McpResponses.ToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);
            }

            return McpResponses.ToolResult(id, MapResponse(toolName, httpResponse.StatusCode, body, out var isError), isError);
        } catch (ArgumentException ex) {
            return McpResponses.ToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return McpResponses.ToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (OperationCanceledException) {
            // Client-side HttpClient timeout (TaskCanceledException : OperationCanceledException):
            // the request never got a server 408, so give the same actionable hint the 408 path
            // does rather than letting it fall through to the generic outer "internal error".
            return McpResponses.ToolResult(id, TimeoutHintFor(toolName), isError: true);
        } catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) {
            // A wrong-typed argument that slipped past the tolerant readers still becomes a tool
            // error the agent can react to — never the generic outer "internal error".
            return McpResponses.ToolResult(id, $"Error: malformed arguments — {ex.Message}", isError: true);
        }
    }

    /// <summary>Maps an /api/analytics response to tool-result text. Success passes the raw JSON
    /// through (plus a truncation trailer — a boolean flag alone is easy for a model to miss);
    /// error statuses become actionable, self-repair-friendly messages.</summary>
    internal static string MapResponse(string toolName, HttpStatusCode status, string body, out bool isError) {
        isError = true;

        switch (status) {
            case HttpStatusCode.OK:
                isError = false;
                return toolName == "get_analytics_schema" ? ExtractSchemaText(body) : AppendTruncationTrailer(body);
            case HttpStatusCode.Unauthorized:
                return NotLoggedInMessage;
            case HttpStatusCode.NotFound:
                return NotSupportedMessage; // server predating the /api/analytics endpoints
            case HttpStatusCode.RequestTimeout:
                return TimeoutHintFor(toolName);
            case HttpStatusCode.BadRequest:
                return $"REJECTED: {ExtractProblemDetail(body)}";
            default:
                // Unlisted statuses (e.g. a 403/429 from the app or an intermediary) still speak
                // RFC-7807 — surface the clean `detail` so the agent gets an actionable reason,
                // not a raw JSON envelope. ExtractProblemDetail falls back to the body when it
                // isn't a problem document (e.g. an HTML 502 from a proxy).
                return $"Error: HTTP {(int)status} — {ExtractProblemDetail(body)}";
        }
    }

    /// <summary>The schema endpoint wraps the document in {"text", "max_rows"} — unwrap it so the
    /// agent receives the document itself, not a JSON envelope.</summary>
    static string ExtractSchemaText(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["text"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static string AppendTruncationTrailer(string body) {
        try {
            var obj = JsonNode.Parse(body)?.AsObject();

            if (obj?["truncated"]?.GetValue<bool>() == true) {
                var maxRows = obj["max_rows"]?.GetValue<int>();
                return $"{body}\nWARNING: result truncated to {maxRows?.ToString() ?? "the server's max"} rows — " +
                       "any statistic computed from these rows is unreliable. Aggregate in SQL (GROUP BY/COUNT/AVG " +
                       "run over ALL rows server-side), add filters, or raise max_rows.";
            }
        } catch {
            // fall through to the raw body
        }

        return body;
    }

    /// <summary>400 bodies are RFC-7807 problem documents; the `detail` field carries the
    /// validator's machine-consumable rejection reason the agent self-repairs from.</summary>
    static string ExtractProblemDetail(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["detail"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // NOTE: request-body keys are snake_case — the server's global JSON policy.
    internal static JsonObject BuildQueryBody(JsonObject? args, string? cwdRepoHash) {
        if (!TryGetStringArg(args, "sql", out var sql))
            throw new ArgumentException("sql is required and must be a non-empty string.");

        // scope is optional (defaults to repo), but a present-yet-wrong-typed value is a mistake
        // the agent should hear about precisely rather than as an opaque failure.
        var scope = "repo";
        if (args?["scope"] is { } scopeNode) {
            if (scopeNode is not JsonValue sv || !sv.TryGetValue<string>(out var scopeStr))
                throw new ArgumentException("scope must be a string — 'repo' (default) or 'global'.");
            scope = scopeStr;
        }

        // Fail closed rather than silently widening scope: an unresolvable cwd repo must never
        // default to the tenant-wide route (mirrors McpMemoryServer.BuildSaveBody).
        var repos = scope switch {
            "repo" when cwdRepoHash is null => throw new ArgumentException(
                "Cannot resolve the current repository — run from a git checkout or pass scope: 'global' to query all repositories."),
            "repo"   => new JsonArray(JsonValue.Create(cwdRepoHash)),
            "global" => null,
            _        => throw new ArgumentException($"Unknown scope: '{scope}' — use 'repo' (default) or 'global'.")
        };

        var body = new JsonObject {
            ["sql"]   = sql,
            ["repos"] = repos
        };

        if (McpToolArguments.TryReadIntLenient(args, "max_rows", out var maxRows)) body["max_rows"] = maxRows;

        return body;
    }

    /// <summary>Reads a required string argument tolerantly: returns false (rather than throwing)
    /// when the key is absent, the wrong JSON type, or blank. Mirrors McpReviewServer.</summary>
    static bool TryGetStringArg(JsonObject? args, string name, out string value) {
        value = "";

        if (args?[name] is not JsonValue node) return false;
        if (!node.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return false;

        value = s;

        return true;
    }

    internal static McpTool[] BuildToolsList() => [
        new("get_analytics_schema",
            "The governed analytics schema: the v_an_* views and columns you may query, a terminology glossary, " +
            "SQL rules, and worked examples. Always call this once before writing SQL for query_analytics. " +
            "Covers sessions, tool/skill/token usage, cost, code changes, commits, PRs, evals, and work items.",
            new("object", new(), []), McpToolAnnotations.Read),
        new("query_analytics",
            "Run one governed read-only Postgres SELECT over the analytics views (see get_analytics_schema). " +
            "Answers questions about coding-agent sessions, usage, cost, and delivery telemetry. A rejected " +
            "query returns the reason — fix the SQL and retry. Results are row-capped and flagged when truncated.",
            new("object", new() {
                ["sql"]      = new("string", "A single Postgres SELECT over the governed v_an_* views."),
                ["scope"]    = new("string", "'repo' (default) = only the current repository, derived from the working directory; 'global' = all repositories in the org."),
                ["max_rows"] = new("number", "Row cap for this query (default 300; clamped to the server's maximum).")
            }, ["sql"]), McpToolAnnotations.Read)
    ];
}
