using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemDeclaredDto([property: JsonPropertyName("work_item_id")] string WorkItemId);
