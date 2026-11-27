using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>Both switches for one type; the server replaces the pair.</summary>
public sealed record NotificationPrefUpdateRequestDto(
    [property: JsonPropertyName("enabled")]       bool Enabled,
    [property: JsonPropertyName("slack_enabled")] bool SlackEnabled);
