using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemSessionRequestDto([property: JsonPropertyName("session_id")] string SessionId);
