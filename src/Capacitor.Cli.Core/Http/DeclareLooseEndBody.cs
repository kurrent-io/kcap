using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A loose end on a session the caller owns, held out of next work until <see cref="NotBefore"/>.</summary>
public record DeclareLooseEndBody(
    [property: JsonPropertyName("text")]       string         Text,
    [property: JsonPropertyName("session_id")] string         SessionId,
    [property: JsonPropertyName("not_before")] DateTimeOffset NotBefore);
