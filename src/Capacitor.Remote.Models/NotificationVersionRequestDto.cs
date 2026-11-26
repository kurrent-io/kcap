using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NotificationVersionRequestDto([property: JsonPropertyName("version")] long Version);
