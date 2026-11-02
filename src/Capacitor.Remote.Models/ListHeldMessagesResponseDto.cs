using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The owner gets every open message and those decided in the last day; a sender gets only their own. Oldest first.
public sealed record ListHeldMessagesResponseDto {
    [JsonPropertyName("messages")] public ListedHeldMessageDto[] Messages { get; init; } = [];
}
