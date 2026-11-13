using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts;

/// The Capacitor-specific events more than one vendor produces, under the JSON names the server
/// persists them with.
[JsonSerializable(typeof(ContextCompactedEvent))]
[JsonSerializable(typeof(UserMessageRetractedEvent))]
public sealed partial class TranscriptEventJsonContext : JsonSerializerContext;
