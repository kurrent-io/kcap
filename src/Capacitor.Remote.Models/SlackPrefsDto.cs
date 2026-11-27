using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>Whether Slack can deliver. Delivery is connected, needs_reconnect or not_connected.</summary>
public sealed record SlackPrefsDto {
    [JsonPropertyName("tenant_configured")] public bool    TenantConfigured { get; init; }
    [JsonPropertyName("delivery")]          public string? Delivery         { get; init; }
}
