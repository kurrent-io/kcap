using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <param name="WorkItemsNotLinked">Each work item reference a publish sent that the server did not link.</param>
public record ArtefactDetailDto(
    [property: JsonPropertyName("artefact")]              ArtefactDto   Artefact,
    [property: JsonPropertyName("work_items_not_linked")] List<string>? WorkItemsNotLinked = null);
