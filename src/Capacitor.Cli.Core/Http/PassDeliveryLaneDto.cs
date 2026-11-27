using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>What one lane did with a pass: <see cref="Lane"/> is <c>bell</c>, <c>slack</c> or <c>agent</c>, and
/// <see cref="Status"/> <c>delivered</c>, <c>waiting</c> or <c>skipped</c>. <see cref="Text"/> is the server's own words for it.</summary>
public record PassDeliveryLaneDto(
    [property: JsonPropertyName("lane")]       string          Lane,
    [property: JsonPropertyName("status")]     string          Status,
    [property: JsonPropertyName("at")]         DateTimeOffset? At,
    [property: JsonPropertyName("reason")]     string?         Reason,
    [property: JsonPropertyName("session_id")] string?         SessionId,
    [property: JsonPropertyName("text")]       string          Text);
