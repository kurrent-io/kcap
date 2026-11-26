using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>Which optional surfaces the server offers its caller. A server too old to name a surface omits it, which
/// reads as not offered.</summary>
public sealed record UserSurfacesResponseDto {
    [JsonPropertyName("passes")]        public bool Passes        { get; init; }
    [JsonPropertyName("notifications")] public bool Notifications { get; init; }
}
