using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>What the sender's answer did: <see cref="Outcome"/> is <c>sent</c>, <c>declined</c>, <c>expired</c>,
/// <c>voided</c> or <c>pending</c>. <see cref="AppliedGrants"/> landed and stay, like any share.</summary>
public record PassApprovalResponseDto(
    [property: JsonPropertyName("outcome")]              string                     Outcome,
    [property: JsonPropertyName("state")]                string                     State,
    [property: JsonPropertyName("applied_grants")]       List<PassSharingGrantDto>? AppliedGrants,
    [property: JsonPropertyName("follow_up_not_before")] DateTimeOffset?            FollowUpNotBefore,
    [property: JsonPropertyName("reason")]               string?                    Reason);
