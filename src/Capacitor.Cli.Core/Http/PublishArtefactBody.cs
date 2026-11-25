using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A publish names no audience: the server refuses one, and a page is shared afterwards through <see cref="ISharingApi"/>.</summary>
public record PublishArtefactBody(
    [property: JsonPropertyName("title")]       string        Title,
    [property: JsonPropertyName("html")]        string        Html,
    [property: JsonPropertyName("description")] string?       Description,
    [property: JsonPropertyName("sources")]     List<string>? Sources,
    [property: JsonPropertyName("kind")]        string?       Kind      = null,
    [property: JsonPropertyName("work_items")]  List<string>? WorkItems = null);
