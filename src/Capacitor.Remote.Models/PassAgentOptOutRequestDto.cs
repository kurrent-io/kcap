using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record PassAgentOptOutRequestDto([property: JsonPropertyName("opted_out")] bool OptedOut);
