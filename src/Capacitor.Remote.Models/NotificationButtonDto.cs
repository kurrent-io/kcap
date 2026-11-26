using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>An answer a notification offers. Style is default, primary or danger.</summary>
public sealed record NotificationButtonDto {
    [JsonPropertyName("answer")] public string Answer { get; init; } = "";
    [JsonPropertyName("label")]  public string Label  { get; init; } = "";
    [JsonPropertyName("style")]  public string Style  { get; init; } = "default";
}
