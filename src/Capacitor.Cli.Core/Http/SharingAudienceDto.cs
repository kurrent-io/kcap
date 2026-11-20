using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>Someone an object can be shared with. <see cref="Key"/> is opaque: pass it back as it came, never build one.
/// <see cref="Kind"/> may be one this binary does not know.</summary>
public record SharingAudienceDto(
    [property: JsonPropertyName("key")]        string  Key,
    [property: JsonPropertyName("name")]       string  Name,
    [property: JsonPropertyName("kind")]       string  Kind,
    [property: JsonPropertyName("detail")]     string? Detail,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("inactive")]   bool    Inactive);
