using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A pass the caller released to their coding agent.</summary>
public record CatchUpPassDto(
    [property: JsonPropertyName("pass_id")]        string            PassId,
    [property: JsonPropertyName("kind")]           string            Kind,
    [property: JsonPropertyName("item_kind")]      string?           ItemKind,
    [property: JsonPropertyName("item_id")]        string?           ItemId,
    [property: JsonPropertyName("sent_at")]        DateTimeOffset    SentAt,
    [property: JsonPropertyName("colleague_text")] ColleagueTextDto? ColleagueText);
