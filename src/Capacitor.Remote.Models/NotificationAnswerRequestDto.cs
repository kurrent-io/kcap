using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>An answer to a notification: one of its buttons' answers, or "reply" with the typed text.</summary>
public sealed record NotificationAnswerRequestDto(
    [property: JsonPropertyName("answer")] string  Answer,
    [property: JsonPropertyName("text")]   string? Text);
