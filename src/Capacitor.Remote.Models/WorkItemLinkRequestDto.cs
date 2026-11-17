using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <see cref="Reference"/> is an issue key or a PR, as a person would type it.
public sealed record WorkItemLinkRequestDto([property: JsonPropertyName("reference")] string Reference);
