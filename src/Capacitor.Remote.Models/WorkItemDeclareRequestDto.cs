using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemDeclareRequestDto(
    [property: JsonPropertyName("new_title")]  string NewTitle,
    [property: JsonPropertyName("session_id")] string SessionId);
