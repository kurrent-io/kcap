using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>One notification type's switches. A type that is not Slack-capable is in-app only.</summary>
public sealed record NotificationTypePrefDto {
    [JsonPropertyName("type")]          public string Type         { get; init; } = "";
    [JsonPropertyName("display_name")]  public string DisplayName  { get; init; } = "";
    [JsonPropertyName("description")]   public string Description  { get; init; } = "";
    [JsonPropertyName("enabled")]       public bool   Enabled      { get; init; }
    [JsonPropertyName("slack_enabled")] public bool   SlackEnabled { get; init; }
    [JsonPropertyName("slack_capable")] public bool   SlackCapable { get; init; } = true;
    [JsonPropertyName("retired")]       public bool   Retired      { get; init; }
}
