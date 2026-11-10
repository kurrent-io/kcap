using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The body of <c>POST /api/next-work/launch</c>. <see cref="Outcome"/> is <c>requested</c>, <c>refused</c>,
/// <c>failed</c> or <c>pending</c>; a pending launch holds the loose-end claim while its delivery is unknown.</summary>
public sealed record NextWorkLaunchResultDto(
    [property: JsonPropertyName("outcome")]            string                           Outcome,
    [property: JsonPropertyName("agent_id")]           string?                          AgentId,
    [property: JsonPropertyName("refusal")]            string?                          Refusal,
    [property: JsonPropertyName("checkouts")]          List<NextWorkLaunchCheckoutDto>? Checkouts,
    [property: JsonPropertyName("loose_end_claim_id")] string?                          LooseEndClaimId = null,
    [property: JsonPropertyName("dispatch_state")]     string?                          DispatchState = null);
