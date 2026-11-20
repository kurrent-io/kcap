using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record SetSharingGrantBody([property: JsonPropertyName("relation")] string Relation);
