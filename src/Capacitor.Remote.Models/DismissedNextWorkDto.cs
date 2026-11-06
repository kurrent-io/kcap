using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record DismissedNextWorkDto(
    [property: JsonPropertyName("target_key")]  string  TargetKey,
    [property: JsonPropertyName("target_kind")] string? TargetKind,
    [property: JsonPropertyName("label")]       string? Label);
