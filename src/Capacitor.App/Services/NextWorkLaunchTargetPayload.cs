using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// The next-work task a composer launch starts, so the server claims its loose end and attaches its work item as the
/// next-work launch does. Sent only for a work item or loose end.
public sealed record NextWorkLaunchTargetPayload([property: JsonPropertyName("target_key")] string TargetKey);
