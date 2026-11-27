using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>One styled span of a notification's text. Style is plain, strong or code.</summary>
public sealed record NotificationRunDto {
    [JsonPropertyName("text")]     public string  Text     { get; init; } = "";
    [JsonPropertyName("style")]    public string  Style    { get; init; } = "plain";
    [JsonPropertyName("url")]      public string? Url      { get; init; }
    [JsonPropertyName("external")] public bool    External { get; init; }
}
