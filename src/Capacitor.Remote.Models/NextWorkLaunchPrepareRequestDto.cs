using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkLaunchPrepareRequestDto([property: JsonPropertyName("target_key")] string TargetKey);
