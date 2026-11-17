using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record WorkItemPartCreatedDto([property: JsonPropertyName("work_item_id")] string WorkItemId);
