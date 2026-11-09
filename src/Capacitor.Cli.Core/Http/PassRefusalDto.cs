using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>Why a pass call was refused, in words for the sender. <see cref="ShareUrl"/> is where the item's sharing is
/// changed, when that is what the sender can do next.</summary>
public record PassRefusalDto(
    [property: JsonPropertyName("code")]      string  Code,
    [property: JsonPropertyName("reason")]    string  Reason,
    [property: JsonPropertyName("share_url")] string? ShareUrl = null);
