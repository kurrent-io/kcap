using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NotificationPrefsDto {
    [JsonPropertyName("types")] public IReadOnlyList<NotificationTypePrefDto> Types { get; init; } = [];
    [JsonPropertyName("slack")] public SlackPrefsDto                          Slack { get; init; } = new();
}
