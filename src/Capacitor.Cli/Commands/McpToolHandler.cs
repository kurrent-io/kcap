using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Answers a <c>tools/call</c> with the server's session client and URL.</summary>
delegate Task<string> McpToolHandler(JsonNode id, JsonObject request, HttpClient client, string baseUrl);
