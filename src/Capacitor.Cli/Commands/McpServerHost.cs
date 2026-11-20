using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>Runs a kcap MCP server over stdio whose tools reach the kcap server with the session's client.</summary>
static class McpServerHost {
    public static async Task<int> RunAsync(McpServerDescriptor server, McpHostServices services, McpToolHandler handle) {
        var (config, profiles, tokens, http, startup, time) = services;
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Best-effort, and recorded even when the read throws: a stale token on disk must never block the server
        // from starting, and "could not tell" belongs with "not logged in".
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }

        // The top-level "mcp" command is denylisted; the reportable "mcp-server" is what lets per-call events leave.
        var telemetry = CliTelemetry.Start(startup with { Command = "mcp-server" }, config, time);
        telemetry.AddSharedProperty("logged_in", loggedIn);

        await using var mcp = new McpTelemetry(telemetry);

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Created on first use, so a session that never calls a tool stays local-only.
        using var sessionClient = new OnDemand<HttpClient>(async () => {
            var created = await http.ForSessionAsync();
            server.ConfigureClient?.Invoke(created);
            return created;
        });

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return McpResponses.ToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                var client = await sessionClient.GetAsync();
                return await handle(callId, callRequest, client, baseUrl);
            } catch (Exception ex) {
                // The detail goes to stderr only: an IO error's message can carry local paths.
                await Console.Error.WriteLineAsync($"kcap mcp {server.LogLabel}: unexpected error handling tools/call: {ex}");
                return McpResponses.ToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
        }

        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = time.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                mcp.ToolCalled(server.Name, tool, ok, CommandTiming.ElapsedMs(start, time));
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        await McpStdioLoop.RunAsync(reader, writer, server.Tools, TimedDispatchToolCallAsync,
            (id, method, request) => Answer(server, id, method, request), server.LogLabel, server.OrderKey);

        return 0;
    }

    static string Answer(McpServerDescriptor server, JsonNode id, string? method, JsonObject request) => method switch {
        null when server.RejectsNonStringMethod => McpResponses.Error(id, -32600, "Invalid request: method must be a string"),
        "initialize"                            => McpResponses.Initialize(id, request, server.Name, server.Instructions),
        "tools/list"                            => McpResponses.ToolsList(id, server.Tools),
        _                                       => McpProtocol.TryHandleStandardMethod(method, id)
                                                   ?? McpResponses.Error(id, -32601, $"Method not found: {method}")
    };
}
