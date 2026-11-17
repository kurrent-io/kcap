using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <see cref="LinkClass"/> is <c>link</c> for identity-bearing evidence or <c>reference</c> for an ambient mention.
/// <see cref="State"/> and <see cref="Title"/> are the tracker's, null until enriched.
public sealed record WorkItemLinkDto {
    [JsonPropertyName("short_key")]  public required string ShortKey { get; init; }
    [JsonPropertyName("url")]        public string? Url { get; init; }
    [JsonPropertyName("title")]      public string? Title { get; init; }
    [JsonPropertyName("state")]      public string? State { get; init; }
    [JsonPropertyName("link_class")] public string LinkClass { get; init; } = "link";
}
