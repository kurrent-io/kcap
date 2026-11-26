using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NotificationUnreadCountDto {
    [JsonPropertyName("count")] public int Count { get; init; }
}
