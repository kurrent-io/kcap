using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>Words a colleague wrote, under <see cref="Label"/>, which says how an agent is to read them.</summary>
public record ColleagueTextDto(
    [property: JsonPropertyName("label")]             string?       Label,
    [property: JsonPropertyName("brief")]             string?       Brief,
    [property: JsonPropertyName("note")]              string?       Note,
    [property: JsonPropertyName("suggested_prompts")] List<string>? SuggestedPrompts);
