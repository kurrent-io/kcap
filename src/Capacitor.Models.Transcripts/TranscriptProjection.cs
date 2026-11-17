using Capacitor.Models.Transcripts.Harness.Antigravity;
using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Capacitor.Models.Transcripts.Harness.Cursor;
using Capacitor.Models.Transcripts.Harness.Gemini;
using Capacitor.Models.Transcripts.Harness.Grok;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Capacitor.Models.Transcripts.Harness.Muse;
using Capacitor.Models.Transcripts.Harness.MuseAssistant;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Capacitor.Models.Transcripts.Harness.Pi;

namespace Capacitor.Models.Transcripts;

/// One transcript line in, canonical events out. Stateful only through the context the caller
/// passes; a projection never mutates an event it has returned.
public interface ITranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}

/// The one registration site: a vendor's projection lives under Harness/&lt;Vendor&gt;/ and is named
/// here, nowhere else.
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor) => vendor.ToLowerInvariant() switch {
        "antigravity" => AntigravityTranscriptEvents.Instance,
        "claude" => ClaudeTranscriptEvents.Instance,
        "codex" => CodexRolloutEvents.Instance,
        "copilot" => CopilotTranscriptEvents.Instance,
        "cursor" => CursorTranscriptEvents.Instance,
        "gemini" => GeminiTranscriptEvents.Instance,
        "grok" => GrokTranscriptEvents.Instance,
        "kiro" => KiroTranscriptEvents.Instance,
        "mistral-vibe" => MistralVibeTranscriptEvents.Instance,
        "muse" => MuseTranscriptEvents.Instance,
        "muse-assistant" => MuseAssistantTranscriptEvents.Instance,
        "opencode" => OpenCodeTranscriptEvents.Instance,
        "pi" => PiTranscriptEvents.Instance,
        _ => null,
    };
}
