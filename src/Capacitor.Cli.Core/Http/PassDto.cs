using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A pass or message as one of its parties reads it; <see cref="Side"/> is <c>sent</c> or
/// <c>received</c>.</summary>
public record PassDto(
    [property: JsonPropertyName("pass_id")]                     string                     PassId,
    [property: JsonPropertyName("side")]                        string                     Side,
    [property: JsonPropertyName("kind")]                        string                     Kind,
    [property: JsonPropertyName("item_kind")]                   string?                    ItemKind,
    [property: JsonPropertyName("item_id")]                     string?                    ItemId,
    [property: JsonPropertyName("sender_id")]                   string                     SenderId,
    [property: JsonPropertyName("recipient_id")]                string                     RecipientId,
    [property: JsonPropertyName("brief")]                       string?                    Brief,
    [property: JsonPropertyName("suggested_prompts")]           List<string>?              SuggestedPrompts,
    [property: JsonPropertyName("note")]                        string?                    Note,
    [property: JsonPropertyName("reply_to")]                    string?                    ReplyTo,
    [property: JsonPropertyName("state")]                       string                     State,
    [property: JsonPropertyName("sent_at")]                     DateTimeOffset             SentAt,
    [property: JsonPropertyName("take_over_source_session_id")] string?                    TakeOverSourceSessionId,
    [property: JsonPropertyName("accepted_at")]                 DateTimeOffset?            AcceptedAt,
    [property: JsonPropertyName("released_to_agent")]           bool?                      ReleasedToAgent,
    [property: JsonPropertyName("follow_up_due")]               bool?                      FollowUpDue,
    [property: JsonPropertyName("applied_grants")]              List<PassSharingGrantDto>? AppliedGrants,
    [property: JsonPropertyName("stay_hidden_session_ids")]     List<string>?              StayHiddenSessionIds);
