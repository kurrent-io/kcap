using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A sharing refusal. <see cref="Code"/> may be one this binary does not know, so callers show <see cref="Message"/>.</summary>
public record SharingErrorDto(
    [property: JsonPropertyName("code")]    string Code,
    [property: JsonPropertyName("message")] string Message);
