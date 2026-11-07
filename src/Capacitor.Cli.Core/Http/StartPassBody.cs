using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One body for a pass and a message. <see cref="Kind"/> is <c>look</c>, <c>message</c>, <c>take_over</c> or
/// <c>pair</c>; <see cref="ItemKind"/> is <c>work_item</c>, <c>session</c>, <c>page</c> or <c>hosted_agent</c>.</summary>
public record StartPassBody(
    [property: JsonPropertyName("kind")]              string        Kind,
    [property: JsonPropertyName("recipient_id")]      string        RecipientId,
    [property: JsonPropertyName("item_kind")]         string?       ItemKind         = null,
    [property: JsonPropertyName("item_id")]           string?       ItemId           = null,
    [property: JsonPropertyName("brief")]             string?       Brief            = null,
    [property: JsonPropertyName("note")]              string?       Note             = null,
    [property: JsonPropertyName("suggested_prompts")] List<string>? SuggestedPrompts = null,
    [property: JsonPropertyName("reminder_off")]      bool?         ReminderOff      = null,
    [property: JsonPropertyName("reply_to")]          string?       ReplyTo          = null);
