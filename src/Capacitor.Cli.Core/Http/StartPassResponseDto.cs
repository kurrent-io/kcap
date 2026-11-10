using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A pass that was sent, or that waits for its sender to approve <see cref="Grants"/>.</summary>
public record StartPassResponseDto(
    [property: JsonPropertyName("pass_id")]              string                     PassId,
    [property: JsonPropertyName("state")]                string                     State,
    [property: JsonPropertyName("grants")]               List<PassSharingGrantDto>? Grants,
    [property: JsonPropertyName("approval_text")]        string?                    ApprovalText,
    [property: JsonPropertyName("expires_at")]           DateTimeOffset?            ExpiresAt,
    [property: JsonPropertyName("follow_up_not_before")] DateTimeOffset?            FollowUpNotBefore);
