using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record PassMutedSenderDto {
    [JsonPropertyName("user_id")] public string UserId { get; init; } = "";
    [JsonPropertyName("name")]    public string Name   { get; init; } = "";
}
