using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>A page of the caller's inbox. The unread count is the inbox total, not this page's.</summary>
public sealed record NotificationPageDto {
    [JsonPropertyName("items")]        public IReadOnlyList<NotificationItemDto> Items       { get; init; } = [];
    [JsonPropertyName("next_cursor")]  public string?                            NextCursor  { get; init; }
    [JsonPropertyName("unread_count")] public int                                UnreadCount { get; init; }
}
