using System.Text.Json.Nodes;

namespace Capacitor.Cli.Ide;

/// <summary>A handler failure the peer is told about with this code, message and data; any other exception is answered as an internal error.</summary>
internal sealed class IdeRpcException(int code, string message, JsonNode? data = null) : Exception(message) {
    public int Code { get; } = code;

    public JsonNode? Detail { get; } = data;
}
