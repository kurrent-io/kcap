using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A refusal from the held-message routes. Error is a stable code; Message is the server's line for a person.
public sealed record AgentPrivilegeErrorDto {
    [JsonPropertyName("error")]   public string Error { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
