using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Data is base64 of the raw bytes for the agent's terminal.
public sealed record SendTerminalInputRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("data")] string Data);
