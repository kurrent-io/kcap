using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemTitleRequestDto([property: JsonPropertyName("title")] string Title);
