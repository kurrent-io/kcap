using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemPartRequestDto([property: JsonPropertyName("title")] string Title);
