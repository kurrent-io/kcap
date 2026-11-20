using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Commands;

/// <summary>The JSON-RPC response lines every kcap MCP server writes.</summary>
static class McpResponses {
    public static string ToolResult(JsonNode id, string text, bool isError = false) =>
        Result<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    public static string Error(JsonNode id, int code, string message) =>
        new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        }.ToJsonString();

    public static string Result<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) =>
        new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        }.ToJsonString();

    public static string Initialize(JsonNode id, JsonObject request, string serverName, string? instructions) =>
        Result<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new(serverName, "1.0.0"), instructions),
            McpJsonContext.Default.McpInitResult
        );

    public static string ToolsList(JsonNode id, McpTool[] tools) =>
        Result(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);
}
