using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>Which optional surfaces the server offers this caller.</summary>
public record UserSurfacesDto([property: JsonPropertyName("passes")] bool Passes);
