using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One prompt the server holds for an agent's next turn: what SubscribeToChat returns and
/// PendingInputChanged pushes. Nothing is required, so one thin item cannot drop a whole push.
public sealed record QueuedInputItem {
    [JsonPropertyName("dispatch_id")]     public Guid DispatchId { get; init; }
    [JsonPropertyName("sender_user_id")]  public string? SenderUserId { get; init; }
    [JsonPropertyName("text")]            public string Text { get; init; } = "";
    [JsonPropertyName("dispatched_at")]   public DateTimeOffset DispatchedAt { get; init; }
    /// Where the message stands: held, with_mod, delivered, rejected or unconfirmed. Absent while a hosted
    /// send is still in flight, and from an older server; a hosted refusal carries <c>rejected</c>.
    [JsonPropertyName("delivery_state")]  public string? DeliveryState { get; init; }
    /// Why a rejected message was rejected; absent otherwise.
    [JsonPropertyName("delivery_reason")] public string? DeliveryReason { get; init; }
}
