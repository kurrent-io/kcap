using System.Text.Json.Serialization;
using Capacitor.Models.Transcripts.Harness.Antigravity;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Capacitor.Models.Transcripts.Harness.Muse;

namespace Capacitor.Models.Transcripts;

/// Every non-protobuf payload `CanonicalEventTypes.Of` names, under the JSON names the server
/// persists them with.
[JsonSerializable(typeof(ContextCompactedEvent))]
[JsonSerializable(typeof(UserMessageRetractedEvent))]
[JsonSerializable(typeof(CodexUsageBackfilledEvent))]
[JsonSerializable(typeof(KiroUsageBackfill))]
[JsonSerializable(typeof(MuseUsageBackfill))]
[JsonSerializable(typeof(AntigravityUsageBackfill))]
[JsonSerializable(typeof(CopilotUsageBackfill))]
[JsonSerializable(typeof(CopilotSessionMetrics))]
public sealed partial class TranscriptEventJsonContext : JsonSerializerContext;
