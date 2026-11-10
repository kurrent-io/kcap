using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The body of <c>POST /api/next-work/launch/prepare</c>: a refusal code, or the repository the target
/// lives in, the viewer's checkouts of it and the prompt the agent will be given.</summary>
public sealed record NextWorkLaunchPreparationDto(
    [property: JsonPropertyName("refusal")]        string?                              Refusal,
    [property: JsonPropertyName("repo")]           string?                              Repo,
    [property: JsonPropertyName("checkouts")]      List<NextWorkLaunchCheckoutDto>?     Checkouts,
    [property: JsonPropertyName("probe_failures")] List<NextWorkLaunchProbeFailureDto>? ProbeFailures,
    [property: JsonPropertyName("prompt")]         string?                              Prompt);
