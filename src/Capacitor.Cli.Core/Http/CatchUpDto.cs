using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>What the caller's agent may read of the passes they received: <see cref="Notices"/> holds one fixed notice
/// per pass not yet released, carrying nothing anyone wrote.</summary>
public record CatchUpDto(
    [property: JsonPropertyName("notices")]  List<string>?         Notices,
    [property: JsonPropertyName("released")] List<CatchUpPassDto>? Released);
