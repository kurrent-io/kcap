using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One sharing write a pass makes: <see cref="Access"/> is the text the sender is shown, and
/// <see cref="AlsoAllows"/> what the write lets the recipient do beyond seeing the item.</summary>
public record PassSharingGrantDto(
    [property: JsonPropertyName("item_kind")]   string  ItemKind,
    [property: JsonPropertyName("item_id")]     string  ItemId,
    [property: JsonPropertyName("access")]      string  Access,
    [property: JsonPropertyName("also_allows")] string? AlsoAllows);
