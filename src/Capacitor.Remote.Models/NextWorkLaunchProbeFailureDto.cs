using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkLaunchProbeFailureDto(
    [property: JsonPropertyName("daemon_name")] string DaemonName,
    [property: JsonPropertyName("reason")]      string Reason);
