using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>Whether the answer was taken, and the sentence to show either way.</summary>
public sealed record NotificationAnswerResponseDto {
    [JsonPropertyName("accepted")] public bool   Accepted { get; init; }
    [JsonPropertyName("message")]  public string Message  { get; init; } = "";
}
