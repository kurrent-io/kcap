namespace Capacitor.Models.Transcripts.Harness.Grok;

/// A turn's last assistant text, or its last tool call while it has no text.
public sealed record GrokTurnAnchor(Guid EventId, string EventType);
