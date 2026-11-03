using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// State is held, approved, dispatched, rejected, withdrawn or discarded. Outcome is sent, not_delivered or
/// delivery_unknown once a dispatched message's delivery is known. SenderName is null for a sender with no account.
public sealed record ListedHeldMessageDto {
    [JsonPropertyName("dispatch_id")]    public Guid DispatchId { get; init; }
    [JsonPropertyName("sender_user_id")] public string SenderUserId { get; init; } = "";
    [JsonPropertyName("sender_name")]    public string? SenderName { get; init; }
    [JsonPropertyName("text")]           public string Text { get; init; } = "";
    [JsonPropertyName("state")]          public string State { get; init; } = "";
    [JsonPropertyName("outcome")]        public string? Outcome { get; init; }
    [JsonPropertyName("held_at")]        public DateTimeOffset HeldAt { get; init; }
    [JsonPropertyName("attachments")]    public ListedHeldAttachmentDto[] Attachments { get; init; } = [];
}
